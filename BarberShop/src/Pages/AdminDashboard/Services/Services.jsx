import { Plus, Trash2, X, Check, Scissors } from "lucide-react";
import { useState, useContext, useRef } from "react";
import "./Services.css";
import ServiceCard from "./ServiceCard/ServiceCard.jsx";
import useFetch from "../../../Hooks/useFetch";
import { adminAxios } from "../../../Hooks/AxiosInterceptor";
import { ToastContext } from "../../../Context/ToastContext";
import LoadingSpinner from "../../../Components/LoadingSpinner/LoadingSpinner";
import { resolveServiceImage } from "../../../utils/serviceImage.js";

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
    const { data, loading } = useFetch("/api/services/admin", true);
    const [services, setServices] = useState([]);
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

    /* Shared field validation for create and edit. Returns an error string, or null when valid. */
    const validateFields = ({ title, description, price, duration }) => {
        if (title.trim().length < NAME_MIN) return `Service name must be at least ${NAME_MIN} characters`;
        if (title.trim().length > NAME_MAX) return `Service name must be at most ${NAME_MAX} characters`;
        if (!description.trim()) return "Description is required";
        if (description.trim().length > DESC_MAX) return `Description must be at most ${DESC_MAX} characters`;
        const priceNum = Number(price);
        if (!Number.isFinite(priceNum) || priceNum < PRICE_MIN || priceNum > PRICE_MAX)
            return `Price must be between ${PRICE_MIN} and ${PRICE_MAX}`;
        const durationNum = Number(duration);
        if (!Number.isInteger(durationNum) || durationNum < DURATION_MIN || durationNum > DURATION_MAX)
            return `Duration must be a whole number between ${DURATION_MIN} and ${DURATION_MAX} minutes`;
        return null;
    };

    const resetCreate = () => {
        setTitle("");
        setDescription("");
        setPrice("");
        setDuration("");
        setNewImage("");
        setImageFile(null);
        setShowCreate(false);
    };

    const handleCreate = async (e) => {
        e.preventDefault();
        const error = validateFields({ title, description, price, duration });
        if (error) { showToast(error); return; }
        if (!imageFile) { showToast("A service image is required"); return; }
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
            showToast("Service created", `${res.data.name} was added to your catalogue.`);
        }
        catch (err) {
            showToast(err.response?.data?.message || "Something went wrong. Please try again.");
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
    };

    const closeEdit = () => {
        setEditService(null);
        setEditImageFile(null);
        setEditImage("");
    };

    const handleEdit = async (e) => {
        e.preventDefault();
        const error = validateFields({ title: editTitle, description: editDescription, price: editPrice, duration: editDuration });
        if (error) { showToast(error); return; }
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
            showToast("Service updated", `${res.data.name} was saved.`);
        }
        catch (err) {
            showToast(err.response?.data?.message || "Something went wrong. Please try again.");
        }
    };

    const handleDelete = async (id) => {
        try {
            await adminAxios.delete(`/api/services/delete/${id}`);
            setServices((prev) => prev.filter((s) => s.id !== id));
            setDeleteServiceId(null);
            showToast("Service deleted", "The service was removed from your catalogue.");
        }
        catch (err) {
            showToast(err.response?.data?.message || "An unexpected error occurred");
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

    return (
        <>
            <div className="team-header">
                <div>
                    <h2 className="team-title">MANAGE SERVICES</h2>
                    <p className="team-subtitle">Create, edit and remove the services you offer</p>
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
                            </div>
                            <div className="form-group">
                                <label className="form-label">Name *</label>
                                <input className="form-input" placeholder="e.g. Haircut & Beard Trim"
                                    maxLength={NAME_MAX} value={title} onChange={(e) => setTitle(e.target.value)} />
                            </div>
                            <div className="form-group">
                                <label className="form-label">Description *</label>
                                <textarea className="form-input" rows={3} placeholder="Describe what's included"
                                    maxLength={DESC_MAX} value={description} onChange={(e) => setDescription(e.target.value)} />
                            </div>
                            <div className="services-form-row">
                                <div className="form-group">
                                    <label className="form-label">Price (&euro;) *</label>
                                    <input className="form-input" type="number" min={PRICE_MIN} max={PRICE_MAX} step="0.01"
                                        inputMode="decimal" placeholder="0.00" value={price}
                                        onChange={(e) => setPrice(e.target.value)} />
                                </div>
                                <div className="form-group">
                                    <label className="form-label">Duration (min) *</label>
                                    <input className="form-input" type="number" min={DURATION_MIN} max={DURATION_MAX} step="1"
                                        inputMode="numeric" placeholder="30" value={duration}
                                        onChange={(e) => setDuration(e.target.value)} />
                                </div>
                            </div>
                        </div>
                        <div className="modal-footer">
                            <button className="btn-secondary" onClick={resetCreate}>Cancel</button>
                            <button className="btn-primary" onClick={handleCreate}><Plus size={16} /></button>
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
                                <input className="form-input" maxLength={NAME_MAX}
                                    value={editTitle} onChange={(e) => setEditTitle(e.target.value)} />
                            </div>
                            <div className="form-group">
                                <label className="form-label">Description *</label>
                                <textarea className="form-input" rows={3} maxLength={DESC_MAX}
                                    value={editDescription} onChange={(e) => setEditDescription(e.target.value)} />
                            </div>
                            <div className="services-form-row">
                                <div className="form-group">
                                    <label className="form-label">Price (&euro;) *</label>
                                    <input className="form-input" type="number" min={PRICE_MIN} max={PRICE_MAX} step="0.01"
                                        inputMode="decimal" value={editPrice} onChange={(e) => setEditPrice(e.target.value)} />
                                </div>
                                <div className="form-group">
                                    <label className="form-label">Duration (min) *</label>
                                    <input className="form-input" type="number" min={DURATION_MIN} max={DURATION_MAX} step="1"
                                        inputMode="numeric" value={editDuration} onChange={(e) => setEditDuration(e.target.value)} />
                                </div>
                            </div>
                        </div>
                        <div className="modal-footer">
                            <button className="btn-secondary" onClick={closeEdit}>Cancel</button>
                            <button className="btn-primary" onClick={handleEdit}><Check size={16} /></button>
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
                            <button className="btn-secondary" onClick={() => setDeleteServiceId(null)}>Cancel</button>
                            <button className="btn-primary" style={{ background: "#e74c3c" }}
                                onClick={() => handleDelete(deleteServiceId)}><Trash2 size={16} /></button>
                        </div>
                    </div>
                </div>
            )}
        </>
    );
};
export default Services;
