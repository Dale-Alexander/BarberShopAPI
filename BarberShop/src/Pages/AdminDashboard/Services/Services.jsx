import { Plus, Trash2, X, Check, Scissors } from "lucide-react";
import { useState, useContext, useRef } from "react";
import "./Services.css";
import ServiceCard from "./ServiceCard/ServiceCard.jsx";
import useFetch from "../../../Hooks/useFetch";
import { adminAxios } from "../../../Hooks/AxiosInterceptor";
import { ToastContext } from "../../../Context/ToastContext";
import LoadingSpinner from "../../../Components/LoadingSpinner/LoadingSpinner";
import ErrorState from "../../../Components/ErrorState/ErrorState";
import { PulseLoader } from "react-spinners";
import { resolveServiceImage } from "../../../utils/serviceImage.js";
import { getErrorMessage } from "../../../utils/errorMessage.js";

/* Client-side bounds mirror the backend (CreateServiceViewModel / UpdateServiceViewModel data
 * annotations) so a bad value is caught before the round-trip. The server still re-validates. */
const NAME_MIN = 2;
const NAME_MAX = 100;
const DESC_MAX = 255;
const PRICE_MIN = 0.01;
const PRICE_MAX = 1000;
const DURATION_MIN = 1;
const DURATION_MAX = 300;

const Services = () => {
    const { data, loading, error, reFetch } = useFetch("/api/services/admin", true);
    const [services, setServices] = useState([]);
    // Guards the create/edit/delete modal actions (only one is open at a time) against a double-submit.
    const [submitting, setSubmitting] = useState(false);
    const { showToast } = useContext(ToastContext);

    // Create flow
    const [showCreate, setShowCreate] = useState(false);
    const [title, setTitle] = useState("");
    const [description, setDescription] = useState("");
    const [price, setPrice] = useState("");
    const [duration, setDuration] = useState("");
    const [newImage, setNewImage] = useState("");   // preview URL (<img> can't take a raw File as src)
    const [imageFile, setImageFile] = useState(null); // raw file sent to the backend
    const [dragActive, setDragActive] = useState(false);
    const [createAttempted, setCreateAttempted] = useState(false); // reveals required-field errors after a submit try
    const createModalContainerRef = useRef(null);

    // Edit flow — kept separate so its fields never collide with the create modal.
    const [editService, setEditService] = useState(null); // row being edited (null = closed)
    const [editTitle, setEditTitle] = useState("");
    const [editDescription, setEditDescription] = useState("");
    const [editPrice, setEditPrice] = useState("");
    const [editDuration, setEditDuration] = useState("");
    const [editImage, setEditImage] = useState("");      // preview (existing photo or freshly picked file)
    const [editImageFile, setEditImageFile] = useState(null); // raw new file, only if the admin picks one
    const [editDragActive, setEditDragActive] = useState(false);
    const [editAttempted, setEditAttempted] = useState(false);
    const editModalContainerRef = useRef(null);

    const [deleteServiceId, setDeleteServiceId] = useState(null);

    /* Copy the fetched catalogue into local editable state so create/edit/delete update the grid
     * without a refetch. Done as a render-phase adjustment (React's recommended alternative to a
     * setState-in-effect) which resyncs only when a genuinely new fetch result arrives. */
    const [syncedData, setSyncedData] = useState(null);
    if (data !== syncedData) {
        setSyncedData(data);
        setServices(data ?? []);
    }

    /* Per-field validators mirroring the backend (Create/UpdateServiceViewModel). Each returns an error
     * string, or null when valid. Unlike person names, a service name can contain digits ("Kids Cut 12"),
     * so only length is checked. Description's only rule is that it isn't empty (its 255-char cap is
     * already enforced by the textarea's maxLength). */
    const titleErr = (v) => {
        const t = (v ?? "").trim();
        if (!t) return "Service name is required";
        if (t.length < NAME_MIN) return `Service name must be at least ${NAME_MIN} characters`;
        if (t.length > NAME_MAX) return `Service name must be at most ${NAME_MAX} characters`;
        return null;
    };
    const descErr = (v) => ((v ?? "").trim() ? null : "Description is required");
    const priceErr = (v) => {
        if (v === "" || v == null) return "Price is required";
        const n = Number(v);
        if (!Number.isFinite(n) || n < PRICE_MIN || n > PRICE_MAX)
            return `Price must be between ${PRICE_MIN} and ${PRICE_MAX}`;
        return null;
    };
    const durationErr = (v) => {
        if (v === "" || v == null) return "Duration is required";
        const n = Number(v);
        if (!Number.isInteger(n) || n < DURATION_MIN || n > DURATION_MAX)
            return `Duration must be a whole number between ${DURATION_MIN} and ${DURATION_MAX} minutes`;
        return null;
    };

    /* Inline errors. A field's error shows once it has a value (live as they type) or once they've tried
     * to submit (so empty required fields light up too). The image is required only on create. */
    const cTitleError = (title || createAttempted) ? titleErr(title) : null;
    const cDescError = (description || createAttempted) ? descErr(description) : null;
    const cPriceError = (price || createAttempted) ? priceErr(price) : null;
    const cDurationError = (duration || createAttempted) ? durationErr(duration) : null;
    const cImageError = createAttempted && !imageFile ? "A service image is required" : null;

    const eTitleError = (editTitle || editAttempted) ? titleErr(editTitle) : null;
    const eDescError = (editDescription || editAttempted) ? descErr(editDescription) : null;
    const ePriceError = (editPrice || editAttempted) ? priceErr(editPrice) : null;
    const eDurationError = (editDuration || editAttempted) ? durationErr(editDuration) : null;

    const resetCreate = () => {
        setTitle("");
        setDescription("");
        setPrice("");
        setDuration("");
        setNewImage("");
        setImageFile(null);
        setCreateAttempted(false);
        setShowCreate(false);
    };

    const handleCreate = async (e) => {
        e.preventDefault();
        setCreateAttempted(true); // surface any empty-required errors inline
        const error = titleErr(title) || descErr(description) || priceErr(price) || durationErr(duration);
        if (error || !imageFile) return; // inline errors are now visible; don't round-trip
        if (submitting) return;
        setSubmitting(true);
        try {
            const formData = new FormData();
            formData.append("Title", title.trim());
            formData.append("Description", description.trim());
            formData.append("Price", Number(price));
            formData.append("DurationMin", Number(duration));
            formData.append("ImageFile", imageFile);

            const res = await adminAxios.post("/api/services/create-service", formData);
            setServices((prev) => [...prev, res.data]);
            resetCreate();
            showToast("Service created", `${res.data.name} was added to your catalogue.`, "success");
        }
        catch (err) {
            showToast("Couldn't create service", getErrorMessage(err));
        }
        finally {
            setSubmitting(false);
        }
    };

    const openEdit = (service) => {
        setEditService(service);
        setEditTitle(service.name ?? "");
        setEditDescription(service.description ?? "");
        setEditPrice(service.price != null ? String(service.price) : "");
        setEditDuration(service.durationMin != null ? String(service.durationMin) : "");
        setEditImage(service.imageUrl ? resolveServiceImage(service.imageUrl) : "");
        setEditImageFile(null);
        setEditAttempted(false);
    };

    const closeEdit = () => {
        setEditService(null);
        setEditImageFile(null);
        setEditImage("");
        setEditAttempted(false);
    };

    const handleEdit = async (e) => {
        e.preventDefault();
        setEditAttempted(true);
        const error = titleErr(editTitle) || descErr(editDescription) || priceErr(editPrice) || durationErr(editDuration);
        if (error) return; // inline errors now visible
        if (submitting) return;
        setSubmitting(true);
        try {
            const formData = new FormData();
            formData.append("Title", editTitle.trim());
            formData.append("Description", editDescription.trim());
            formData.append("Price", Number(editPrice));
            formData.append("DurationMin", Number(editDuration));
            // Services always have an image; the edit flow can only replace it, never clear it. Send a
            // new file only if the admin picked one - otherwise the PATCH leaves the existing image alone.
            if (editImageFile) formData.append("ImageFile", editImageFile);

            const res = await adminAxios.patch(`/api/services/update-service/${editService.id}`, formData);
            setServices((prev) => prev.map((s) => s.id === editService.id ? { ...s, ...res.data } : s));
            closeEdit();
            showToast("Service updated", `${res.data.name} was saved.`, "success");
        }
        catch (err) {
            showToast("Couldn't update service", getErrorMessage(err));
        }
        finally {
            setSubmitting(false);
        }
    };

    const handleDelete = async (id) => {
        if (submitting) return;
        setSubmitting(true);
        try {
            await adminAxios.delete(`/api/services/delete/${id}`);
            setServices((prev) => prev.filter((s) => s.id !== id));
            setDeleteServiceId(null);
            showToast("Service deleted", "The service was removed from your catalogue.", "success");
        }
        catch (err) {
            showToast("Couldn't delete service", getErrorMessage(err));
        }
        finally {
            setSubmitting(false);
        }
    };

    const handleImageChange = (e) => {
        const file = e.target.files[0];
        if (file) { setImageFile(file); setNewImage(URL.createObjectURL(file)); }
    };

    const handleDrop = (e) => {
        e.preventDefault();
        const file = e.dataTransfer.files[0];
        if (file) { setImageFile(file); setNewImage(URL.createObjectURL(file)); }
    };

    const handleEditImageChange = (e) => {
        const file = e.target.files[0];
        if (file) { setEditImageFile(file); setEditImage(URL.createObjectURL(file)); }
    };

    const handleEditDrop = (e) => {
        e.preventDefault();
        const file = e.dataTransfer.files[0];
        if (file) { setEditImageFile(file); setEditImage(URL.createObjectURL(file)); }
    };

    if (loading) {
        return <LoadingSpinner message="Loading Services" color="#e0e0e0" />;
    }

    /* On a failed load, show an error with Retry rather than the "No services yet" empty state below,
       which would wrongly imply the catalogue is empty. */
    if (error) {
        return (
            <ErrorState
                title="Couldn't load services"
                message="We couldn't load your service catalogue. Please try again."
                onRetry={reFetch}
            />
        );
    }

    return (
        <>
            <div className="page-header page-header--inline">
                <div className="page-header-text">
                    <h1 className="page-title">Manage Services</h1>
                    <p className="page-subtitle">Create, edit and remove the services you offer</p>
                </div>
                <button className="create-barber" onClick={() => setShowCreate(true)}>
                    <Plus size={16} />
                    ADD SERVICE
                </button>
            </div>
            <div className="team-content-area">
                <div className="services-grid">
                    {services?.length ? (
                        services.map((s) => (
                            <ServiceCard key={s.id} service={s} onEdit={openEdit} setDeleteServiceId={setDeleteServiceId} />
                        ))
                    ) : (
                        <div className="team-empty-state">
                            <Scissors size={48} />
                            <h3>No services yet</h3>
                            <p>Get started by adding your first service</p>
                            <button className="create-barber" onClick={() => setShowCreate(true)}>
                                <Plus size={16} />
                                ADD SERVICE
                            </button>
                        </div>
                    )}
                </div>
            </div>

            {showCreate && (
                <div className="modal-overlay"
                    onMouseDown={(e) => (createModalContainerRef.current = e.target)}
                    onClick={(e) => { if (createModalContainerRef.current === e.currentTarget) resetCreate(); }}>
                    <div className="modal-content modal-content--wide" onClick={(e) => e.stopPropagation()}>
                        <div className="modal-header">
                            <h2>Add New Service</h2>
                            <button className="modal-close" onClick={resetCreate}><X size={20} /></button>
                        </div>
                        <div className="modal-body">
                            <div className="form-group">
                                <label className="form-label">Image *</label>
                                <div className={`image-dropzone${dragActive ? " image-dropzone--active" : ""}`}
                                    onDragOver={(e) => { e.preventDefault(); setDragActive(true); }}
                                    onDragLeave={() => setDragActive(false)}
                                    onDrop={(e) => { setDragActive(false); handleDrop(e); }}
                                    onClick={() => document.getElementById("service-image-input")?.click()}>
                                    {newImage ? (
                                        <div className="image-preview-wrap">
                                            <img src={newImage} className="service-image-preview" alt="" />
                                            <button type="button" className="image-preview-remove"
                                                onClick={(e) => { e.stopPropagation(); setNewImage(""); setImageFile(null); }}>
                                                <X size={14} />
                                            </button>
                                        </div>
                                    ) : (
                                        <div className="image-dropzone-placeholder">
                                            <Plus size={24} />
                                            <span>Drag & Drop or click to upload</span>
                                        </div>
                                    )}
                                    <input id="service-image-input" type="file" accept="image/*"
                                        style={{ display: "none" }} onChange={handleImageChange} />
                                </div>
                                {cImageError && <span className="form-error">{cImageError}</span>}
                            </div>
                            <div className="form-group">
                                <label className="form-label">Name *</label>
                                <input className={`form-input${cTitleError ? " form-input--invalid" : ""}`} placeholder="e.g. Haircut & Beard Trim"
                                    maxLength={NAME_MAX} value={title} onChange={(e) => setTitle(e.target.value)} />
                                {cTitleError && <span className="form-error">{cTitleError}</span>}
                            </div>
                            <div className="form-group">
                                <label className="form-label">Description *</label>
                                <textarea className={`form-input${cDescError ? " form-input--invalid" : ""}`} rows={3} placeholder="Describe what's included"
                                    maxLength={DESC_MAX} value={description} onChange={(e) => setDescription(e.target.value)} />
                                {cDescError && <span className="form-error">{cDescError}</span>}
                            </div>
                            <div className="services-form-row">
                                <div className="form-group">
                                    <label className="form-label">Price (&euro;) *</label>
                                    <input className={`form-input${cPriceError ? " form-input--invalid" : ""}`} type="number" min={PRICE_MIN} max={PRICE_MAX} step="0.01"
                                        inputMode="decimal" placeholder="0.00" value={price}
                                        onChange={(e) => setPrice(e.target.value)} />
                                    {cPriceError && <span className="form-error">{cPriceError}</span>}
                                </div>
                                <div className="form-group">
                                    <label className="form-label">Duration (min) *</label>
                                    <input className={`form-input${cDurationError ? " form-input--invalid" : ""}`} type="number" min={DURATION_MIN} max={DURATION_MAX} step="1"
                                        inputMode="numeric" placeholder="30" value={duration}
                                        onChange={(e) => setDuration(e.target.value)} />
                                    {cDurationError && <span className="form-error">{cDurationError}</span>}
                                </div>
                            </div>
                        </div>
                        <div className="modal-footer">
                            <button className="btn-secondary" onClick={resetCreate}>Cancel</button>
                            <button className="btn-primary" onClick={handleCreate} disabled={submitting}>{submitting ? <PulseLoader size={8} color="hsl(220, 25%, 10%)" /> : <Plus size={16} />}</button>
                        </div>
                    </div>
                </div>
            )}

            {editService && (
                <div className="modal-overlay"
                    onMouseDown={(e) => (editModalContainerRef.current = e.target)}
                    onClick={(e) => { if (editModalContainerRef.current === e.currentTarget) closeEdit(); }}>
                    <div className="modal-content modal-content--wide" onClick={(e) => e.stopPropagation()}>
                        <div className="modal-header">
                            <h2>Edit Service</h2>
                            <button className="modal-close" onClick={closeEdit}><X size={20} /></button>
                        </div>
                        <div className="modal-body">
                            <div className="form-group">
                                <label className="form-label">Image</label>
                                <div className={`image-dropzone${editDragActive ? " image-dropzone--active" : ""}`}
                                    onDragOver={(e) => { e.preventDefault(); setEditDragActive(true); }}
                                    onDragLeave={() => setEditDragActive(false)}
                                    onDrop={(e) => { setEditDragActive(false); handleEditDrop(e); }}
                                    onClick={() => document.getElementById("service-edit-image-input")?.click()}>
                                    {editImage ? (
                                        <div className="image-preview-wrap">
                                            <img src={editImage} className="service-image-preview" alt="" />
                                        </div>
                                    ) : (
                                        <div className="image-dropzone-placeholder">
                                            <Plus size={24} />
                                            <span>Drag & Drop or click to upload</span>
                                        </div>
                                    )}
                                    <input id="service-edit-image-input" type="file" accept="image/*"
                                        style={{ display: "none" }} onChange={handleEditImageChange} />
                                </div>
                            </div>
                            <div className="form-group">
                                <label className="form-label">Name *</label>
                                <input className={`form-input${eTitleError ? " form-input--invalid" : ""}`} maxLength={NAME_MAX}
                                    value={editTitle} onChange={(e) => setEditTitle(e.target.value)} />
                                {eTitleError && <span className="form-error">{eTitleError}</span>}
                            </div>
                            <div className="form-group">
                                <label className="form-label">Description *</label>
                                <textarea className={`form-input${eDescError ? " form-input--invalid" : ""}`} rows={3} maxLength={DESC_MAX}
                                    value={editDescription} onChange={(e) => setEditDescription(e.target.value)} />
                                {eDescError && <span className="form-error">{eDescError}</span>}
                            </div>
                            <div className="services-form-row">
                                <div className="form-group">
                                    <label className="form-label">Price (&euro;) *</label>
                                    <input className={`form-input${ePriceError ? " form-input--invalid" : ""}`} type="number" min={PRICE_MIN} max={PRICE_MAX} step="0.01"
                                        inputMode="decimal" value={editPrice} onChange={(e) => setEditPrice(e.target.value)} />
                                    {ePriceError && <span className="form-error">{ePriceError}</span>}
                                </div>
                                <div className="form-group">
                                    <label className="form-label">Duration (min) *</label>
                                    <input className={`form-input${eDurationError ? " form-input--invalid" : ""}`} type="number" min={DURATION_MIN} max={DURATION_MAX} step="1"
                                        inputMode="numeric" value={editDuration} onChange={(e) => setEditDuration(e.target.value)} />
                                    {eDurationError && <span className="form-error">{eDurationError}</span>}
                                </div>
                            </div>
                        </div>
                        <div className="modal-footer">
                            <button className="btn-secondary" onClick={closeEdit}>Cancel</button>
                            <button className="btn-primary" onClick={handleEdit} disabled={submitting}>{submitting ? <PulseLoader size={8} color="hsl(220, 25%, 10%)" /> : <Check size={16} />}</button>
                        </div>
                    </div>
                </div>
            )}

            {deleteServiceId && (
                <div className="modal-overlay" onClick={() => setDeleteServiceId(null)}>
                    <div className="modal-content" onClick={(e) => e.stopPropagation()} style={{ maxWidth: 420 }}>
                        <div className="modal-header">
                            <h2>Confirm Deletion</h2>
                            <button className="modal-close" onClick={() => setDeleteServiceId(null)}><X size={20} /></button>
                        </div>
                        <div className="modal-body">
                            <p style={{ color: "var(--muted-fg)", lineHeight: 1.6 }}>
                                Are you sure you want to delete this service? It will be removed from your
                                catalogue. Past bookings that used it are kept intact.
                            </p>
                        </div>
                        <div className="modal-footer">
                            <button className="btn-secondary" onClick={() => setDeleteServiceId(null)} disabled={submitting}>Cancel</button>
                            <button className="btn-primary" style={{ background: "#e74c3c" }} disabled={submitting}
                                onClick={() => handleDelete(deleteServiceId)}>{submitting ? <PulseLoader size={8} color="#fff" /> : <Trash2 size={16} />}</button>
                        </div>
                    </div>
                </div>
            )}
        </>
    );
};
export default Services;
