// Service images are stored either as a full external URL or as a local "/uploads/..." path served
// by the backend, so every service <img> should route through here to prepend the API base for the
// local case. A stored file can also go missing, so pair it with handleServiceImageError below.
const PLACEHOLDER = "/images/service-placeholder.svg";

export function resolveServiceImage(url) {
    if (!url) return PLACEHOLDER;
    return url.startsWith("http") ? url : `${import.meta.env.VITE_BASE_URL}${url}`;
}

// Use as the <img onError> handler so a resolved-but-broken image (e.g. the file was deleted
// server-side) falls back to the placeholder. Guarded so a missing placeholder can't loop.
export function handleServiceImageError(e) {
    const img = e.currentTarget;
    if (img.dataset.fallbackApplied) return;
    img.dataset.fallbackApplied = "true";
    img.src = PLACEHOLDER;
}
