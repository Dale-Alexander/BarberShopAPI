import { Pencil, Trash2, Clock } from "lucide-react";
import "./ServiceCard.css";
import { resolveServiceImage, handleServiceImageError } from "../../../../utils/serviceImage.js";
import { formatEuro } from "../../../../utils/money.js";

const ServiceCard = ({ service, onEdit, setDeleteServiceId }) => {
    return (
        <div className="service-card">
            <div className="service-card-img">
                <img src={resolveServiceImage(service.imageUrl)} onError={handleServiceImageError} alt={service.name} />
            </div>
            <div className="service-card-info">
                <div className="service-card-heading">
                    <h3 className="service-card-name">{service.name}</h3>
                    <span className="service-card-price">{formatEuro(service.price)}</span>
                </div>
                <p className="service-card-duration">
                    <Clock size={14} /> {service.durationMin} min
                </p>
                <p className="service-card-description">{service.description}</p>
            </div>
            <div className="service-card-actions">
                <button className="service-action-btn service-action-edit"
                    onClick={() => onEdit(service)}
                    title="Edit Service">
                    <Pencil size={16} />
                </button>
                <button className="service-action-btn service-action-delete"
                    onClick={() => setDeleteServiceId(service.id)}
                    title="Delete Service">
                    <Trash2 size={16} />
                </button>
            </div>
        </div>
    )
}
export default ServiceCard;
