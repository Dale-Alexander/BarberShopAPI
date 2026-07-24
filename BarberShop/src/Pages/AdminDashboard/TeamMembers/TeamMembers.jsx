import { Plus, Trash2, X, Users, Check, RotateCcw, UserX, Mail, Phone } from "lucide-react";
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
    /* Set when a deactivate is refused with 409 because the barber has upcoming bookings: holds the
     * backend's { message, conflicts } plus the barberId so the admin can review who'd be cancelled and
     * re-submit with confirmCancelBookings. Null = no conflict pending. */
    const [deactivateConflict, setDeactivateConflict] = useState(null);
    const [email, setEmail] = useState("");
    // Server-side "email already in use" (409). Shown inline under the Email field rather than as a
    // toast, since it's a fix-this-field problem and the admin should keep what they typed.
    const [emailConflict, setEmailConflict] = useState(null);
    const [newImage, setNewImage] = useState("");/* needed because <img> cannot use a raw image file as src. URL.createObjectURL converts the file into something the image can display */
    const [imageFile, setImageFile] = useState();/* the raw file which we send to the backend. We cant send the useState("image") because "image" is a URL 
stored i the browser's memory which the backend cant access*/
    const [dragActive, setDragActive] = useState(false);
    // Guards all three modal actions (create/edit/delete) - only one modal is open at a time - so a
    // slow request can't be fired twice (a double-submitted create would make two barbers).
    const [submitting, setSubmitting] = useState(false);
    const { showToast } = useContext(ToastContext);
    const createModalContainerRef = useRef(null);

    // Active / Inactive tabs. The list endpoint returns both, so switching is a pure client-side
    // filter - no refetch. Active is the default because it's the working roster; deactivated barbers
    // accumulate over time and would otherwise dilute the grid.
    const [activeTab, setActiveTab] = useState("active");

    // Reactivate reuses the create modal (and the backend's revive-by-email branch in CreateBarber),
    // so this holds the barber being brought back; null = the modal is in plain "add" mode.
    const [reactivateBarber, setReactivateBarber] = useState(null);
    const isReactivate = !!reactivateBarber;

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
            // Soft delete server-side, so flip the flag in place rather than dropping the row - the card
            // moves to the Inactive tab, where it can still be viewed or reactivated.
            setBarbers(barbers.map(b => b.id === id ? { ...b, isActive: false } : b));
            setDeleteBarberId(null);
        }
        catch(err){
            // Barber has upcoming bookings: the backend refuses to silently orphan them and hands back the
            // affected list. Swap the are-you-sure modal for the conflict modal so the admin can review who'd
            // be cancelled/refunded and confirm.
            if (err.response?.status === 409 && err.response.data?.requiresConfirmation) {
                setDeleteBarberId(null);
                setDeactivateConflict({ barberId: id, ...err.response.data });
            } else {
                showToast("Couldn't delete barber", getErrorMessage(err));
            }
        }
        finally{
            setSubmitting(false);
        }
    }

    // Second step of the conflict flow: re-run the deactivate with confirmCancelBookings=true, which cancels
    // & refunds the upcoming bookings and notifies those customers.
    const confirmDeactivateWithCancellations = async () => {
        if (!deactivateConflict || submitting) return;
        const id = deactivateConflict.barberId;
        setSubmitting(true);
        try {
            await adminAxios.delete(`/api/barbers/delete/${id}?confirmCancelBookings=true`);
            setBarbers(barbers.map(b => b.id === id ? { ...b, isActive: false } : b));
            const callList = deactivateConflict.conflicts.filter(c => !c.willBeEmailed);
            showToast(
                "Barber deactivated",
                callList.length > 0
                    ? `${deactivateConflict.conflicts.length} booking(s) cancelled. Please phone the ${callList.length} customer(s) with no email on file.`
                    : `${deactivateConflict.conflicts.length} booking(s) cancelled and those customers emailed.`,
                "info"
            );
            setDeactivateConflict(null);
        }
        catch (err) {
            showToast("Couldn't deactivate barber", getErrorMessage(err));
        }
        finally {
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
        else if(!isReactivate && newImage?.trim()){
            /* Skipped when reactivating: there, newImage holds the barber's *existing* photo resolved to
               an absolute URL purely for the preview. Sending it back would rewrite the stored path as an
               http URL. Sending nothing means the backend keeps the photo it already has. */
            formData.append("ImageUrl", newImage.trim());
        }

        const barber = await adminAxios.post("/api/barbers/create-barber",formData);
        const row = barber.data;
        if (isReactivate) {
            // Same row, brought back to life - merge the server's canonical values in place instead of
            // appending, then follow the barber to the tab they just moved to.
            setBarbers(barbers.map(b => b.id === reactivateBarber.id
                ? { ...b, firstName: row.firstName, lastName: row.lastName, imageUrl: row.imageUrl,
                    email: row.email, totalBookings: row.totalBookings, isActive: true }
                : b));
            setActiveTab("active");
        }
        else {
            setBarbers([...(barbers ?? []), row]);
        }
        closeCreate();
    }
    catch(err){
        /* Inline-under-the-field only makes sense when the admin can edit the field. While reactivating
           the email is locked, and a 409 there means someone else already revived this barber in another
           session - nothing to retype, so it goes to a toast like any other failure. */
        if (err.response?.status === 409 && !isReactivate) {
            setEmailConflict(getErrorMessage(err));
        } else {
            showToast(isReactivate ? "Couldn't reactivate barber" : "Couldn't add barber",
                getErrorMessage(err, "Something went wrong. Please refresh or try a different email."));
        }
    }
    finally{
        setSubmitting(false);
    }
    }

    const openCreate = () => {
        setReactivateBarber(null);
        setNewName("");
        setEmail("");
        setPassword("");
        setNewImage("");
        setImageFile(null);
        setEmailConflict(null);
        setShowCreate(true);
    }

    /* Reactivating posts to create-barber with the barber's original email, which the backend routes to
       its revive branch: it flips isActive back, resets the name/photo, and bumps TokenVersion so the
       barber must log in with the password set here. Hence email is prefilled and locked (changing it
       would create a second barber instead of reviving this one) and password is required. */
    const openReactivate = (barber) => {
        setReactivateBarber(barber);
        setNewName(`${barber.firstName ?? ""} ${barber.lastName ?? ""}`.trim());
        setEmail(barber.email ?? "");
        setPassword("");
        // Preview only - never posted back. See the ImageUrl guard in handleCreate.
        setNewImage(barber.imageUrl ? resolveBarberImage(barber.imageUrl) : "");
        setImageFile(null);
        setEmailConflict(null);
        setShowCreate(true);
    }

    const closeCreate = () => {
        setShowCreate(false);
        setReactivateBarber(null);
        setNewName("");
        setEmail("");
        setPassword("");
        setNewImage("");
        setImageFile(null);
        setEmailConflict(null);
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
    // Photo is optional (Barber.ImageUrl is nullable server-side; barbers with no photo fall back to the
    // placeholder), so it isn't part of createValid - a barber can be created without one.
    const createValid = !validateNameInline(newName) && !validateEmail(email)
        && !validatePassword(password);
    const editNameError = editName ? validateNameInline(editName) : null;

    const activeBarbers = barbers?.filter(b => b.isActive) ?? [];
    const inactiveBarbers = barbers?.filter(b => !b.isActive) ?? [];
    const visibleBarbers = activeTab === "active" ? activeBarbers : inactiveBarbers;

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
                <button className="create-barber" onClick={openCreate}>
                    <Plus size={16} />
                    ADD BARBER
                </button>
            </div>
            <div className="team-content-area">
                <div className="team-tabs" role="tablist">
                    <button role="tab"
                        aria-selected={activeTab === "active"}
                        className={`team-tab${activeTab === "active" ? " team-tab--selected" : ""}`}
                        onClick={() => setActiveTab("active")}>
                        Active <span className="team-tab-count">{activeBarbers.length}</span>
                    </button>
                    <button role="tab"
                        aria-selected={activeTab === "inactive"}
                        className={`team-tab${activeTab === "inactive" ? " team-tab--selected" : ""}`}
                        onClick={() => setActiveTab("inactive")}>
                        Inactive <span className="team-tab-count">{inactiveBarbers.length}</span>
                    </button>
                </div>
                <div className="team-grid">
                    {visibleBarbers.length ? (
                        visibleBarbers.map((b) => (
                        <MemberCard key={b.id} barber={b} setDeleteBarberId = {setDeleteBarberId} onEdit={openEdit} onReactivate={openReactivate}/>
                        ))) : activeTab === "inactive" ? (
                            /* Deliberately no action button - the way a barber lands here is by being
                               deleted from the Active tab, so there's nothing to do from an empty one. */
                            <div className="team-empty-state">
                                <UserX size={48} />
                                <h3>No deactivated barbers</h3>
                                <p>Barbers you delete will appear here, and can be brought back</p>
                            </div>
                    ) : (
                            <div className="team-empty-state">
                                <Users size={48} />
                                {inactiveBarbers.length ? (
                                    /* Not "no barbers yet" - there are barbers, they're just all
                                       deactivated, and reactivating one is the likelier fix. */
                                    <>
                                        <h3>No active barbers</h3>
                                        <p>Add a barber, or bring one back from the Inactive tab</p>
                                    </>
                                ) : (
                                    <>
                                        <h3>No barbers yet</h3>
                                        <p>Get started by adding your first team member</p>
                                    </>
                                )}
                                <button className="create-barber" onClick={openCreate}>
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
                            closeCreate();
                        }
                    }}
                >
                    <div className="modal-content modal-content--wide"
                        onClick={(e) => e.stopPropagation()}>
                        <div className="modal-header">
                            <h2>{isReactivate ? "Reactivate Barber" : "Add New Barber"}</h2>
                            <button className="modal-close" onClick={closeCreate}>
                                <X size={20} />
                            </button>
                        </div>
                        <div className="modal-body">
                            {isReactivate && (
                                <p className="modal-note">
                                    This will restore {reactivateBarber.firstName}'s profile and past bookings.
                                    They'll need to log in with the password you set below.
                                </p>
                            )}
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
                                            {/* Hidden when this is the barber's existing photo being shown for
                                                reactivation: create-barber has no "remove image" flag, so the X
                                                would clear the preview without clearing anything server-side.
                                                Uploading a replacement works; removing outright is Edit's job,
                                                available again once they're active. */}
                                            {!(isReactivate && !imageFile) && (
                                                <button type="button"
                                                    className="image-preview-remove"
                                                    onClick={(e) => {
                                                        e.stopPropagation();
                                                        setNewImage(null);
                                                        setImageFile(null);
                                                    }}>
                                                    <X size={14} />
                                                </button>
                                            )}
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
                                {/* Locked when reactivating - the email is what identifies which barber the
                                    backend revives, so editing it would silently create a second one. */}
                                <input className={`form-input${(createEmailError || emailConflict) ? " form-input--invalid" : ""}`}
                                    placeholder="Enter barber's Email"
                                    value={email}
                                    readOnly={isReactivate}
                                    disabled={isReactivate}
                                    onChange={(e) => { setEmail(e.target.value); setEmailConflict(null); }} />
                                {(createEmailError || emailConflict) && <span className="form-error">{createEmailError || emailConflict}</span>}
                            </div>
                            <div className="form-group">
                                <label className="form-label">{isReactivate ? "New Password *" : "Password *"}</label>
                                <input className={`form-input${createPasswordError ? " form-input--invalid" : ""}`}
                                    type="password"
                                    placeholder={isReactivate ? "Set a new password" : "Enter barber's Password"}
                                    value={password}
                                    onChange={(e) => setPassword(e.target.value)} />
                                {createPasswordError && <span className="form-error">{createPasswordError}</span>}
                            </div>
                        </div>
                        <div className="modal-footer">
                            <button className="btn-secondary" onClick={closeCreate}>Cancel</button>
                            <button className="btn-primary" onClick={handleCreate} disabled={!createValid || submitting}>{submitting ? <PulseLoader size={8} color="hsl(220, 25%, 10%)" /> : isReactivate ? <RotateCcw size={16} /> : <Plus size={16} />}</button>
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

            {/* Deactivate blocked by upcoming bookings: review who'd be cancelled/refunded, then confirm. */}
            {deactivateConflict && (
                <div className="modal-overlay" onClick={() => !submitting && setDeactivateConflict(null)}>
                    <div className="modal-content" onClick={(e) => e.stopPropagation()} style={{ maxWidth: 560 }}>
                        <div className="modal-header">
                            <h2>Upcoming bookings for this barber</h2>
                            <button className="modal-close" onClick={() => !submitting && setDeactivateConflict(null)}>
                                <X size={20} />
                            </button>
                        </div>
                        <div className="modal-body">
                            <p style={{ color: "var(--muted-fg)", lineHeight: 1.6, marginBottom: 16 }}>{deactivateConflict.message}</p>

                            <ul style={{ listStyle: "none", margin: 0, padding: 0, display: "flex", flexDirection: "column", gap: 8, maxHeight: 260, overflowY: "auto" }}>
                                {deactivateConflict.conflicts.map((c) => (
                                    <li key={c.id} style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12, padding: "8px 12px", border: "1px solid var(--border, #e5e5e5)", borderRadius: 6 }}>
                                        <div style={{ display: "flex", flexDirection: "column" }}>
                                            <span style={{ fontWeight: 600 }}>{c.date} · {c.time}</span>
                                            {c.customer && <span style={{ color: "var(--muted-fg)", fontSize: 13 }}>{c.customer}</span>}
                                        </div>
                                        {c.willBeEmailed ? (
                                            <span style={{ display: "inline-flex", alignItems: "center", gap: 4, color: "#2e7d32", fontSize: 13, whiteSpace: "nowrap" }}>
                                                <Mail size={14} /> Will be emailed
                                            </span>
                                        ) : (
                                            <span style={{ display: "inline-flex", alignItems: "center", gap: 4, color: "#c9770a", fontSize: 13, whiteSpace: "nowrap" }}>
                                                <Phone size={14} /> {c.phone ? `Call: ${c.phone}` : c.email ? `Email ${c.email}` : "No contact on file"}
                                            </span>
                                        )}
                                    </li>
                                ))}
                            </ul>

                            <p style={{ color: "var(--muted-fg)", fontSize: 13, lineHeight: 1.6, marginTop: 16 }}>
                                Confirming cancels and refunds these bookings and deactivates the barber. Customers with an
                                email are notified automatically; if an email fails to send, that booking appears in your
                                Needs Review list so you can phone them by hand.
                            </p>
                        </div>
                        <div className="modal-footer">
                            <button className="btn-secondary" onClick={() => setDeactivateConflict(null)} disabled={submitting}>Keep barber</button>
                            <button className="btn-primary" style={{ background: "#e74c3c" }} onClick={confirmDeactivateWithCancellations} disabled={submitting}>
                                {submitting ? <PulseLoader size={8} color="#fff" /> : `Cancel ${deactivateConflict.conflicts.length} booking(s) & deactivate`}
                            </button>
                        </div>
                    </div>
                </div>
            )}
        </>
    )
}
export default TeamMembers;