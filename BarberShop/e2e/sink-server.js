/* The two things a browser test cannot otherwise see: the email the customer was sent, and the moment
   Stripe's webhook arrives.

   One process on 5299 doing two jobs, started by Playwright as a webServer entry so its lifetime is
   owned by the run and it cannot outlive it:

   1. A STAND-IN FOR RESEND. The API is pointed here with RESEND_API_URL, so every email the shop sends
      lands in memory instead of a customer's inbox. Specs read them back and assert on the wording -
      "your barber is no longer available" is a promise made to a real person, and until now nothing
      checked it was the sentence that actually went out. Nothing here can reach the internet, which is
      also why RESEND_API_KEY in .env.e2e is a dummy.

   2. A GATE IN FRONT OF THE WEBHOOK. `stripe listen` forwards here rather than straight to the API, and
      a spec can HOLD a delivery, change the shop underneath the customer, then RELEASE it. That gap is
      the only way to reach the webhook's most important branches: a closure (or a deactivation, or an
      hours change) only lands in the refund-and-cancel path when it happens AFTER the card succeeded
      and BEFORE the webhook arrives - about one second in real life. Kill the slot any earlier and
      BookingConflictCanceller voids the still-voidable PaymentIntent, and the customer never pays at
      all, so the branch is never entered.

      The body and the Stripe-Signature header are forwarded BYTE FOR BYTE, so the controller's real
      signature verification runs. Only the timing is under test control - never the content.

   The Stripe CLI is supervised from here too. One process, one lifetime: when Playwright kills this
   server the forwarder dies with it, instead of being left listening against the next run. */

import http from "node:http";
import { spawn, execFileSync } from "node:child_process";

const PORT = Number(process.env.E2E_SINK_PORT ?? 5299);
const API_WEBHOOK_URL = process.env.E2E_API_WEBHOOK_URL ?? "http://localhost:5205/api/webhook";
const EXPECTED_WEBHOOK_SECRET = process.env.E2E_STRIPE_WEBHOOK_SECRET ?? "";

/* ------------------------------------------------------------------ mail sink state */

/** Every email the API has sent since the last clear, newest last. */
const emails = [];
/* "ok" delivers; "fail" answers 500, the way Resend being down looks to EmailService. Left switchable
   because the retry-then-flag path reads that failure, even though no spec drives it to exhaustion -
   Hangfire's backoff between the six attempts is minutes, which no browser test can sit through. */
let emailMode = "ok";

/* ------------------------------------------------------------------ webhook gate state */

let holding = false;
/** Deliveries parked by a hold, released in arrival order. */
const parked = [];
/** What was forwarded and what the API answered - the log a failing spec needs to read. */
const deliveries = [];

/* ------------------------------------------------------------------ stripe cli supervision */

/* settled once we know either that the forwarder is up or that it never will be. The health endpoint
   stays down until then, so Playwright's readiness check waits for the forwarder too and no card spec
   can start against a webhook that isn't being delivered yet. */
let stripe = { ready: false, settled: false, reason: "starting", process: null };

function stripeSecretMatches() {
    if (!EXPECTED_WEBHOOK_SECRET) return "STRIPE_WEBHOOK_SECRET is empty in .env.e2e";
    try {
        const printed = execFileSync("stripe", ["listen", "--print-secret"], {
            encoding: "utf8", stdio: ["ignore", "pipe", "pipe"], timeout: 20_000,
        }).trim();
        if (!printed.startsWith("whsec_")) return "`stripe listen --print-secret` returned nothing usable - run `stripe login`";
        /* Checked HERE rather than left to fail at the controller, because a mismatch surfaces as a
           signature rejection buried in the API log while the browser just sits on "Finalising your
           booking..." until it times out - a failure that looks like a product bug and isn't. */
        if (printed !== EXPECTED_WEBHOOK_SECRET)
            return "STRIPE_WEBHOOK_SECRET in .env.e2e is not the CLI's signing secret - run `stripe listen --print-secret` and paste that value in";
        return null;
    } catch (err) {
        return `could not run the Stripe CLI (${err.code === "ENOENT" ? "not on PATH" : err.message})`;
    }
}

function startStripeForwarder() {
    const problem = stripeSecretMatches();
    if (problem) {
        stripe = { ready: false, settled: true, reason: problem, process: null };
        console.log(`[sink] stripe forwarding OFF: ${problem}`);
        console.log(`[sink] card specs will skip; every other spec runs as normal.`);
        return;
    }

    /* Test mode is the default and stays that way - no --live, ever. Narrowed to the one event the
       webhook handles so the log stays readable; payment_intent.created and the charge.* chorus have
       no branch behind them. */
    const child = spawn("stripe", [
        "listen",
        "--forward-to", `http://localhost:${PORT}/stripe-webhook`,
        "--events", "payment_intent.succeeded",
    ], { stdio: ["ignore", "pipe", "pipe"] });

    stripe.process = child;

    /* Kept so a failure can say what the CLI actually complained about. "exited (code 1)" on its own
       sends you reading this file; "unknown flag" or "not logged in" sends you to the fix. */
    let lastOutput = "";
    const watch = (chunk) => {
        const text = chunk.toString().trim();
        if (text) lastOutput = text;
        if (!stripe.settled && /Ready!/.test(text)) {
            stripe = { ...stripe, ready: true, settled: true, reason: "ready" };
            console.log("[sink] stripe listen is forwarding payment_intent.succeeded");
        }
        // Surfaced rather than swallowed: an expired CLI session says so here and nowhere else.
        if (/error|failed|unknown/i.test(text)) console.log(`[sink] stripe: ${text}`);
    };
    child.stdout.on("data", watch);
    child.stderr.on("data", watch);

    child.on("exit", (code) => {
        stripe = {
            ready: false, settled: true, process: null,
            reason: `stripe listen exited (code ${code})${lastOutput ? `: ${lastOutput}` : ""}`,
        };
    });
    child.on("error", (err) => {
        stripe = { ready: false, settled: true, reason: `stripe listen could not start: ${err.message}`, process: null };
    });

    // A forwarder that never announces itself must not hang the whole run - give up and let the card
    // specs skip with a reason instead.
    setTimeout(() => {
        if (!stripe.settled) {
            stripe = { ...stripe, ready: false, settled: true, reason: "stripe listen never reported Ready!" };
        }
    }, 25_000).unref();
}

/* ------------------------------------------------------------------ helpers */

const readBody = (req) => new Promise((resolve, reject) => {
    const chunks = [];
    req.on("data", (c) => chunks.push(c));
    req.on("end", () => resolve(Buffer.concat(chunks)));
    req.on("error", reject);
});

const json = (res, status, payload) => {
    const body = JSON.stringify(payload);
    res.writeHead(status, { "content-type": "application/json", "content-length": Buffer.byteLength(body) });
    res.end(body);
};

/* Forwards one parked or live delivery to the real webhook endpoint and answers the CLI with whatever
   the API said. Relaying the true status matters: a 500 from a failed refund is a real outcome, and
   swallowing it here would make the gate lie about what happened. */
async function forward({ raw, signature, contentType, res }) {
    let record = { at: new Date().toISOString(), type: null, id: null, status: null };
    try {
        const event = JSON.parse(raw.toString("utf8"));
        record.type = event?.type ?? null;
        record.id = event?.data?.object?.id ?? null;
    } catch { /* not JSON we can read - forward it unchanged anyway, that's the point */ }

    try {
        const response = await fetch(API_WEBHOOK_URL, {
            method: "POST",
            headers: { "content-type": contentType, "stripe-signature": signature },
            body: raw,
        });
        record.status = response.status;
        const text = await response.text();
        res.writeHead(response.status, { "content-type": "application/json" });
        res.end(text);
    } catch (err) {
        record.status = `error: ${err.message}`;
        res.writeHead(502, { "content-type": "application/json" });
        res.end(JSON.stringify({ message: `sink could not reach the API: ${err.message}` }));
    }
    deliveries.push(record);
    console.log(`[sink] webhook ${record.type ?? "?"} -> ${record.status}`);
}

/* ------------------------------------------------------------------ server */

const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, `http://localhost:${PORT}`);
    const path = url.pathname;

    // --- Resend's own endpoint, standing in for api.resend.com ---
    if (req.method === "POST" && path === "/emails") {
        const raw = await readBody(req);
        let message = {};
        try { message = JSON.parse(raw.toString("utf8")); } catch { /* recorded as-is below */ }

        const captured = {
            to: [].concat(message.to ?? []),
            from: message.from ?? null,
            subject: message.subject ?? null,
            html: message.html ?? "",
            text: message.text ?? "",
            receivedAt: new Date().toISOString(),
        };
        emails.push(captured);
        console.log(`[sink] email to ${captured.to.join(", ")}: ${captured.subject}`);

        if (emailMode === "fail") return json(res, 500, { message: "Resend unavailable (e2e sink)" });
        // Resend answers with a uuid, and the .NET client parses it as a Guid - anything else throws
        // inside the client and the email looks like it failed when it didn't.
        return json(res, 200, { id: crypto.randomUUID() });
    }

    // --- the gate stripe listen forwards into ---
    if (req.method === "POST" && path === "/stripe-webhook") {
        const raw = await readBody(req);
        const delivery = {
            raw,
            signature: req.headers["stripe-signature"] ?? "",
            contentType: req.headers["content-type"] ?? "application/json",
            res,
        };
        if (holding) {
            parked.push(delivery);
            console.log(`[sink] webhook HELD (${parked.length} waiting)`);
            return; // answered when the spec releases it
        }
        return await forward(delivery);
    }

    // --- control surface, for the specs ---
    if (path === "/_control/health") {
        // Deliberately not ready until the forwarder has settled one way or the other.
        if (!stripe.settled) return json(res, 503, { ready: false });
        return json(res, 200, { ready: true });
    }

    if (path === "/_control/status") {
        return json(res, 200, {
            stripeReady: stripe.ready,
            stripeReason: stripe.reason,
            emailMode,
            holding,
            emailCount: emails.length,
            deliveries,
        });
    }

    if (req.method === "GET" && path === "/_control/emails") {
        const to = url.searchParams.get("to");
        const subject = url.searchParams.get("subject");
        const matches = emails.filter((e) =>
            (!to || e.to.some((address) => address.toLowerCase() === to.toLowerCase())) &&
            (!subject || (e.subject ?? "").toLowerCase().includes(subject.toLowerCase())));
        return json(res, 200, { emails: matches });
    }

    if (req.method === "DELETE" && path === "/_control/emails") {
        emails.length = 0;
        return json(res, 200, { cleared: true });
    }

    if (req.method === "POST" && path === "/_control/email-mode") {
        const body = JSON.parse((await readBody(req)).toString("utf8") || "{}");
        emailMode = body.mode === "fail" ? "fail" : "ok";
        return json(res, 200, { emailMode });
    }

    if (req.method === "POST" && path === "/_control/webhook/hold") {
        holding = true;
        return json(res, 200, { holding });
    }

    if (req.method === "POST" && path === "/_control/webhook/release") {
        holding = false;
        const waiting = parked.splice(0, parked.length);
        // Sequentially, in arrival order: two deliveries racing through the same booking would be a
        // concurrency test nobody asked for.
        for (const delivery of waiting) await forward(delivery);
        return json(res, 200, { released: waiting.length });
    }

    if (req.method === "DELETE" && path === "/_control/deliveries") {
        deliveries.length = 0;
        return json(res, 200, { cleared: true });
    }

    res.writeHead(404, { "content-type": "application/json" });
    res.end(JSON.stringify({ message: `sink has no route for ${req.method} ${path}` }));
});

server.listen(PORT, () => {
    console.log(`[sink] listening on http://localhost:${PORT} (mail sink + webhook gate)`);
    startStripeForwarder();
});

/* Playwright kills this process at the end of the run; take the forwarder with us rather than leaving
   it attached to the account, still listening, waiting to confuse the next run. */
const shutdown = () => {
    try { stripe.process?.kill(); } catch { /* already gone */ }
    server.close();
    process.exit(0);
};
process.on("SIGINT", shutdown);
process.on("SIGTERM", shutdown);
process.on("exit", () => { try { stripe.process?.kill(); } catch { /* already gone */ } });
