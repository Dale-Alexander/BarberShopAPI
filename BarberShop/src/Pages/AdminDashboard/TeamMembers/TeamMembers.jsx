import { Plus, Trash2, X, Users, Check } from "lucide-react";
import { useState , useEffect, useContext, useRef} from "react";
import MemberCard from "./MemberCard/MemberCard";
import "./TeamMembers.css";
import useFetch from "../../../Hooks/useFetch";
import { adminAxios } from "../../../Hooks/AxiosInterceptor";
import { ToastContext } from "../../../Context/ToastContext";
import LoadingSpinner from "../../../Components/LoadingSpinner/LoadingSpinner";
import ErrorState from "../../../Components/ErrorState/ErrorState";
import { PulseLoader } from "react-spinners";
import { resolveBarberImage } from "../../../utils/barberImage.js";
import { validateName, validateNameInline, validateEmail, validatePassword } from "../../../utils/validation.js";
import { getErrorMessage } from "../../../utils/errorMessage.js";
const TeamMembers = () => {
    const {data, loading, error, reFetch} = useFetch("/api/barbers/admin", true);
    const [barbers, setBarbers] = useState([]);
    const [showCreate, setShowCreate] = useState(false);
    const [newName, setNewName] = useState("");
    const [password, setPassword] = useState("");
    const [deleteBarberId, setDeleteBarberId] = useState(null);
    const [email, setEmail] = useState("");
    const [newImage, setNewImage] = useState("");/* needed because <img> cannot use a raw image file as src. URL.createObjectURL converts the file into something the image can display */
    const [imageFile, setImageFile] = useState();/* the raw file which we send to the backend. We cant send the useState("image") because "image" is a URL 
stored i the browser's memory which the backend cant access*/
    const [dragActive, setDragActive] = useState(false);
    // Guards all three modal actions (create/edit/delete) - only one modal is open at a time - so a
    // slow request can't be fired twice (a double-submitted create would make two barbers).
    const [submitting, setSubmitting] = useState(false);
    const { showToast } = useContext(ToastContext);
    const createModalContainerRef = useRef(null);

    // Edit flow. Kept in its own state so the edit modal never collides with the create modal's
    // fields. editBarber holds the row being edited (null = closed).
    const [editBarber, setEditBarber] = useState(null);
    const [editName, setEditName] = useState("");
    const [editImage, setEditImage] = useState("");//preview URL (existing photo or a freshly picked file)
    const [editImageFile, setEditImageFile] = useState(null);//raw new file, only set if the admin picks one
    const [editRemoveImage, setEditRemoveImage] = useState(false);//true = clear the existing photo on save
    const [editDragActive, setEditDragActive] = useState(false);
    const editModalContainerRef = useRef(null);


    useEffect(()=>{
        setBarbers(data);
        console.log(data);
    }, [data])
    useEffect(()=>{
        console.log(deleteBarberId);
    }, [deleteBarberId])

    const handleDelete = async(id) =>{
        if (submitting) return;
        setSubmitting(true);
        try{
            await adminAxios.delete(`/api/barbers/delete/${id}`);
            setBarbers(barbers.filter(b => b.id !== id));
            setDeleteBarberId(null);
        }
        catch(err){
            showToast("Couldn't delete barber", getErrorMessage(err));
        }
        finally{
            setSubmitting(false);
        }
    }
    const handleCreate = async(e) => {
        e.preventDefault();
        // The submit button is disabled until these pass, so this is just a safety net.
        const fieldError = validateName(newName) || validateEmail(email) || validatePassword(password);
        if (fieldError) {
            showToast("Invalid details", fieldError);
            return;
        }

    if (!imageFile && !newImage?.trim()) {
        showToast("Image required", "Please provide an image.");
        return;
    }
    if (submitting) return;
    setSubmitting(true);
    try{
        const formData = new FormData();
        formData.append("FullName", newName.trim());
        formData.append("Email", email.trim());
        formData.append("Password", password.trim());
        if(imageFile){
            formData.append("ImageFile", imageFile);//localUrl
        }
        else if(newImage?.trim()){
            formData.append("ImageUrl", newImage.trim());
        }
        
        const barber = await adminAxios.post("/api/barbers/create-barber",formData);
        console.log(formData.entries());
        console.log(barber, barber.data);
        setBarbers([...barbers, barber.data]);
        setNewName("");
        setNewImage("");
        setImageFile(null);
        setEmail("");
        setPassword("");
        setShowCreate(false);
    }
    catch(err){
        showToast("Couldn't add barber", getErrorMessage(err, "Something went wrong. Please refresh or try a different email."));
    }
    finally{
        setSubmitting(false);
    }
    }

    const openEdit = (barber) => {
        setEditBarber(barber);
        setEditName(`${barber.firstName ?? ""} ${barber.lastName ?? ""}`.trim());
        // Show the current photo resolved to an absolute URL. Empty string -> the dropzone falls back
        // to its upload placeholder, which is the right look for a barber who has no photo.
        setEditImage(barber.imageUrl ? resolveBarberImage(barber.imageUrl) : "");
        setEditImageFile(null);
        setEditRemoveImage(false);
    }

    const closeEdit = () => {
        setEditBarber(null);
        setEditName("");
        setEditImage("");
        setEditImageFile(null);
        setEditRemoveImage(false);
    }

    const handleEditImageChange = (e) => {
        const file = e.target.files[0];
        if (file) {
            setEditImageFile(file);
            setEditImage(URL.createObjectURL(file));
            setEditRemoveImage(false);//picking a new photo overrides a pending removal
        }
    }

    const handleEditDrop = (e) => {
        e.preventDefault();
        const file = e.dataTransfer.files[0];
        if (file) {
            setEditImageFile(file);
            setEditImage(URL.createObjectURL(file));
            setEditRemoveImage(false);//picking a new photo overrides a pending removal
        }
    }

    const handleEditRemoveImage = () => {
        // Clear the preview and flag removal so the backend nulls the column (rather than "leave as-is").
        setEditImage("");
        setEditImageFile(null);
        setEditRemoveImage(true);
    }

    const handleEdit = async (e) => {
        e.preventDefault();
        const nameError = validateName(editName);
        if (nameError) {
            showToast("Invalid name", nameError);
            return;
        }
        if (submitting) return;
        setSubmitting(true);
        try {
            const formData = new FormData();
            formData.append("FullName", editName.trim());
            // Only send an image if the admin actually picked a new one - otherwise the PATCH leaves
            // the existing photo untouched. Email and password aren't editable here.
            if (editImageFile) {
                formData.append("ImageFile", editImageFile);
            }
            else if (editRemoveImage) {
                formData.append("RemoveImage", "true");
            }
            const res = await adminAxios.patch(`/api/barbers/update/${editBarber.id}`, formData);
            // Merge the server's canonical values (name split into first/last, resolved imageUrl) back
            // into the list so the card updates without a refetch.
            setBarbers(barbers.map(b => b.id === editBarber.id
                ? { ...b, firstName: res.data.firstName, lastName: res.data.lastName, imageUrl: res.data.imageUrl }
                : b));
            closeEdit();
        }
        catch (err) {
            showToast("Couldn't update barber", getErrorMessage(err));
        }
        finally {
            setSubmitting(false);
        }
    }

    /*const handleDelete = (id) => {
        setBarbers(barbers.filter((b) => b.id !== id));
    }*/

    const handleImageChange = (e) => {/* this runs when the user selects a file using file input<input type - "file">
        e.target.files[0] is the first selected file
        URL.createObjectUrl(file) creates a temporary local URL that points to that file so you can display it immediately
        in an <img> tag. This function is for the case that the user picks a file from the file explorer and fires when the file is selected */
        const file = e.target.files[0];
        if (file) {
            setImageFile(file);
            setNewImage(URL.createObjectURL(file));
        }
    }


    const handleDrop = (e) => {/* this handles the actual drop event
        e.dataTransfer.files[0] is the first file that was dropped.
        This function is fired when the releases the mouse button over the drop area. This function allows the user to choose a file to drag from anywhere. For example:
        Directly from desktop, file explorer etc */
        e.preventDefault();
        const file = e.dataTransfer.files[0];/* file is a JS object containing name,size, type and the binary data */
        if (file) {/* makes sure the user actually dropped a file and not something like text */
            setImageFile(file)
            setNewImage(URL.createObjectURL(file));/* creates a temporary URL that points to the file on the user's computer
            This lets you display the immage immediately in an <img> tag. The src attribute of <img> expects a URL, a plain object File(like the onse stored in imageFile) does not satisfy this,so the image wont display */
        }
    }
    // Live inline validation (mirrors the backend). Names use the *inline* check, which flags the
    // obvious problems (digits, over-long) but not the 2-char minimum - a too-short name shows only
    // as a toast on submit, so it never disables the button either. Email/password are fully checked.
    const createNameError = newName ? validateNameInline(newName) : null;
    const createEmailError = email ? validateEmail(email) : null;
    const createPasswordError = password ? validatePassword(password) : null;
    const createValid = !validateNameInline(newName) && !validateEmail(email)
        && !validatePassword(password) && !!(imageFile || newImage?.trim());
    const editNameError = editName ? validateNameInline(editName) : null;

    if (loading) {
        return <LoadingSpinner message="Loading Team Data" color="#e0e0e0"/>
    }

    /* Failed load -> error with Retry, not the "No barbers yet" empty state below (which would wrongly
       read as "you have no barbers"). */
    if (error) {
        return (
            <ErrorState
                title="Couldn't load team"
                message="We couldn't load your barbers. Please try again."
                onRetry={reFetch}
            />
        );
    }
    return (
        <>
            <div className="page-header page-header--inline">
                <div className="page-header-text">
                    <h1 className="page-title">Manage Team</h1>
                    <p className="page-subtitle">Welcome to your team members</p>
                </div>
                <button className="create-barber" onClick={() => setShowCreate(true)}>
                    <Plus size={16} />
                    ADD BARBER
                </button>
            </div>
            <div className="team-content-area">
                <div className="team-grid">
                    {barbers?.length ? (
                        barbers?.map((b) => (
                        <MemberCard key={b.id} barber={b} setDeleteBarberId = {setDeleteBarberId} onEdit={openEdit}/>
                        ))) : (
                            <div className="team-empty-state">
                                <Users size={48} />
                                <h3>No barbers yet</h3>
                                <p>Get started by adding your first team member</p>
                                <button className="create-barber" onClick={() => setShowCreate(true)}>
                                    <Plus size={16} />
                                    ADD BARBER
                                </button>
                            </div>
                    )}
                </div>
            </div>
            {showCreate && (
                <div className="modal-overlay"
                    onMouseDown={(e) => createModalContainerRef.current = e.target}
                    onClick={(e) => {
                        if (createModalContainerRef.current === e.currentTarget) {
                            setShowCreate(false);
                        }
                    }}
                >
                    <div className="modal-content modal-content--wide"
                        onClick={(e) => e.stopPropagation()}>
                        <div className="modal-header">
                            <h2>Add New Barber</h2>
                            <button className="modal-close" onClick={() => setShowCreate(false)}>
                                <X size={20} />
                            </button>
                        </div>
                        <div className="modal-body">
                            <div className="form-group">
                                <label className="form-label">Photo(optional)</label>
                                <div className={`image-dropzone${dragActive ? " image-dropzone--active" : ""}`}
                                    onDragOver={(e) => {
                                        e.preventDefault();
                                        setDragActive(true);
                                    }}
                                    onDragLeave={() => setDragActive(false)}
                                    onDrop={(e) => {
                                        setDragActive(false);
                                        handleDrop(e);
                                    }}
                                    onClick={() => document.getElementById("barber-image-input")?.click()}>
                                    {newImage ? (
                                        <div className="image-preview-wrap">
                                            <img src={newImage} className="image-preview" />
                                            <button type="button"
                                                className="image-preview-remove"
                                                onClick={(e) => {
                                                    e.stopPropagation();
                                                    setNewImage(null);
                                                }}>
                                                <X size={14} />
                                            </button>
                                        </div>
                                    ) : (
                                        <div className="image-dropzone-placeholder">
                                            <Plus size={24} />
                                            <span>Drag & Drop or click to upload</span>
                                        </div>
                                    )}
                                    <input
                                        id="barber-image-input"
                                        type="file"
                                        accept="image/*"
                                        style={{ display: "none" }}
                                        onChange={handleImageChange} />
                                </div>
                            </div>
                            <div className="form-group">
                                <label className="form-label">Name *</label>
                                <input className={`form-input${createNameError ? " form-input--invalid" : ""}`}
                                    placeholder="Enter barber's name"
                                    value={newName}
                                    onChange={(e) => setNewName(e.target.value)} />
                                {createNameError && <span className="form-error">{createNameError}</span>}
                            </div>
                            <div className="form-group">
                                <label className="form-label">Email *</label>
                                <input className={`form-input${createEmailError ? " form-input--invalid" : ""}`}
                                    placeholder="Enter barber's Email"
                                    value={email}
                                    onChange={(e) => setEmail(e.target.value)} />
                                {createEmailError && <span className="form-error">{createEmailError}</span>}
                            </div>
                            <div className="form-group">
                                <label className="form-label">Password *</label>
                                <input className={`form-input${createPasswordError ? " form-input--invalid" : ""}`}
                                    type="password"
                                    placeholder="Enter barber's Password"
                                    value={password}
                                    onChange={(e) => setPassword(e.target.value)} />
                                {createPasswordError && <span className="form-error">{createPasswordError}</span>}
                            </div>
                        </div>
                        <div className="modal-footer">
                            <button className="btn-secondary" onClick={() => setShowCreate(false)}>Cancel</button>
                            <button className="btn-primary" onClick={handleCreate} disabled={!createValid || submitting}>{submitting ? <PulseLoader size={8} color="hsl(220, 25%, 10%)" /> : <Plus size={16} />}</button>
                        </div>
                    </div>
                </div>
            )}
            {editBarber && (
                <div className="modal-overlay"
                    onMouseDown={(e) => editModalContainerRef.current = e.target}
                    onClick={(e) => {
                        if (editModalContainerRef.current === e.currentTarget) {
                            closeEdit();
                        }
                    }}
                >
                    <div className="modal-content modal-content--wide"
                        onClick={(e) => e.stopPropagation()}>
                        <div className="modal-header">
                            <h2>Edit Barber</h2>
                            <button className="modal-close" onClick={closeEdit}>
                                <X size={20} />
                            </button>
                        </div>
                        <div className="modal-body">
                            <div className="form-group">
                                <label className="form-label">Photo</label>
                                <div className={`image-dropzone${editDragActive ? " image-dropzone--active" : ""}`}
                                    onDragOver={(e) => {
                                        e.preventDefault();
                                        setEditDragActive(true);
                                    }}
                                    onDragLeave={() => setEditDragActive(false)}
                                    onDrop={(e) => {
                                        setEditDragActive(false);
                                        handleEditDrop(e);
                                    }}
                                    onClick={() => document.getElementById("barber-edit-image-input")?.click()}>
                                    {editImage ? (
                                        <div className="image-preview-wrap">
                                            <img src={editImage} className="image-preview" />
                                            <button type="button"
                                                className="image-preview-remove"
                                                onClick={(e) => {
                                                    e.stopPropagation();
                                                    handleEditRemoveImage();
                                                }}>
                                                <X size={14} />
                                            </button>
                                        </div>
                                    ) : (
                                        <div className="image-dropzone-placeholder">
                                            <Plus size={24} />
                                            <span>Drag & Drop or click to upload</span>
                                        </div>
                                    )}
                                    <input
                                        id="barber-edit-image-input"
                                        type="file"
                                        accept="image/*"
                                        style={{ display: "none" }}
                                        onChange={handleEditImageChange} />
                                </div>
                            </div>
                            <div className="form-group">
                                <label className="form-label">Name *</label>
                                <input className={`form-input${editNameError ? " form-input--invalid" : ""}`}
                                    placeholder="Enter barber's name"
                                    value={editName}
                                    onChange={(e) => setEditName(e.target.value)} />
                                {editNameError && <span className="form-error">{editNameError}</span>}
                            </div>
                        </div>
                        <div className="modal-footer">
                            <button className="btn-secondary" onClick={closeEdit}>Cancel</button>
                            <button className="btn-primary" onClick={handleEdit} disabled={!!validateNameInline(editName) || submitting}>{submitting ? <PulseLoader size={8} color="hsl(220, 25%, 10%)" /> : <Check size={16} />}</button>
                        </div>
                    </div>
                </div>
            )}
            {deleteBarberId &&(
                        <div className="modal-overlay" onClick={() => setDeleteBarberId(null)}>
                        <div className="modal-content" onClick={(e) => e.stopPropagation()} style={{ maxWidth: 420 }}>
                          <div className="modal-header">
                            <h2>Confirm Deletion</h2>
                            <button className="modal-close" onClick={() => setDeleteBarberId(null)}>
                              <X size={20} />
                            </button>
                          </div>
                          <div className="modal-body">
                            <p style={{ color: "var(--muted-fg)", lineHeight: 1.6 }}>
                              Are you sure you want to delete this barber? This action cannot be undone.
                            </p>
                          </div>
                          <div className="modal-footer">
                            <button className="btn-secondary" onClick={() => setDeleteBarberId(null)} disabled={submitting}>Cancel</button>
                            <button className="btn-primary" style={{ background: "#e74c3c" }} onClick={() => handleDelete(deleteBarberId)} disabled={submitting}>{submitting ? <PulseLoader size={8} color="#fff" /> : <Trash2 size = {16}/>}</button>
                          </div>
                        </div>
                      </div>
            )}
        </>
    )
}
export default TeamMembers;