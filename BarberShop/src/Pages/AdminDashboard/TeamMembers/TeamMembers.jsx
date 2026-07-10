import { Plus, Trash2, X, Users } from "lucide-react";
import { useState , useEffect, useContext, useRef} from "react";
import MemberCard from "./MemberCard/MemberCard";
import "./TeamMembers.css";
import useFetch from "../../../Hooks/useFetch";
import { adminAxios } from "../../../Hooks/AxiosInterceptor";
import { ToastContext } from "../../../Context/ToastContext";
import LoadingSpinner from "../../../Components/LoadingSpinner/LoadingSpinner";
const TeamMembers = () => {
    const {data, loading} = useFetch("/api/barbers/admin", true);
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
    const { showToast } = useContext(ToastContext);
    const createModalContainerRef = useRef(null);


    useEffect(()=>{
        setBarbers(data);
        console.log(data);
    }, [data])
    useEffect(()=>{
        console.log(deleteBarberId);
    }, [deleteBarberId])

    const handleDelete = async(id) =>{
        try{
            await adminAxios.delete(`/api/barbers/delete/${id}`);
            setBarbers(barbers.filter(b => b.id !== id));
            setDeleteBarberId(null);
        }
        catch(err){
            if(err.response?.data?.message){
                showToast(err.response.data.message);
            }
            else{
                showToast("An unexpected error occurred");
            }
        }
    }
    const handleCreate = async(e) => {
        e.preventDefault();
        if(!newName.trim() || !email.trim() || !password.trim()){
            showToast("Missing some fields");
            return;
        }

    if (!imageFile && !newImage?.trim()) {
        showToast("Please provide an image");
        return;
    }
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
        if(err.response?.data?.message){
            showToast(err.response.data.message);
        }
        else{
            showToast("Something went wrong. Please refresh or try inputting a different email");
        }
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
    if (loading) {
        return <LoadingSpinner message="Loading Team Data" color="#e0e0e0"/>
    }
    return (
        <>
            <div className="team-header">
                <div>
                    <h2 className="team-title">MANAGE TEAM</h2>
                    <p className="team-subtitle">Welcome to your team members</p>
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
                        <MemberCard key={b.id} barber={b} setDeleteBarberId = {setDeleteBarberId}/>
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
                                <input className="form-input"
                                    placeholder="Enter barber's name"
                                    value={newName}
                                    onChange={(e) => setNewName(e.target.value)} />
                            </div>
                            <div className="form-group">
                                <label className="form-label">Email *</label>
                                <input className="form-input"
                                    placeholder="Enter barber's Email"
                                    value={email}
                                    onChange={(e) => setEmail(e.target.value)} />
                            </div>
                            <div className="form-group">
                                <label className="form-label">Password *</label>
                                <input className="form-input"
                                    placeholder="Enter barber's Password"
                                    value={password}
                                    onChange={(e) => setPassword(e.target.value)} />
                            </div>
                        </div>
                        <div className="modal-footer">
                            <button className="btn-secondary" onClick={() => setShowCreate(false)}>Cancel</button>
                            <button className="btn-primary" onClick={handleCreate}><Plus size={16} /></button>
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
                            <button className="btn-secondary" onClick={() => setDeleteBarberId(null)}>Cancel</button>
                            <button className="btn-primary" style={{ background: "#e74c3c" }} onClick={() => handleDelete(deleteBarberId)}><Trash2 size = {16}/></button>
                          </div>
                        </div>
                      </div>
            )}
        </>
    )
}
export default TeamMembers;