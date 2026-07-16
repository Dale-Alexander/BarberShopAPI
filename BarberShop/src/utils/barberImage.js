// Barbers may have no photo (Barber.ImageUrl is nullable on the backend), and a stored file can go
// missing, so every barber <img> should route through here. Without it, `url.startsWith(...)` throws
// on a null image and white-screens the page - this returns a placeholder instead.
const PLACEHOLDER = "/images/barber-placeholder.svg";

export function resolveBarberImage(url) {
    if (!url) return PLACEHOLDER;
    return url.startsWith("http") ? url : `${import.meta.env.VITE_BASE_URL}${url}`;
}

// Use as the <img onError> handler so a resolved-but-broken image (e.g. the file was deleted
// server-side) also falls back to the placeholder. Guarded so a missing placeholder can't loop.
export function handleBarberImageError(e) {
    const img = e.currentTarget;
    if (img.dataset.fallbackApplied) return;
    img.dataset.fallbackApplied = "true";
    img.src = PLACEHOLDER;
}
