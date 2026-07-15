import { ChevronLeft, ChevronRight } from "lucide-react";
import "./Pagination.css";

/* Shared pager for the server-paginated booking tables. Renders nothing for a single page so short lists
 * stay clean. onChange is handed the target page number; the parent refetches from it. */
const Pagination = ({ page, totalPages, onChange }) => {
    if (!totalPages || totalPages <= 1) return null;
    return (
        <div className="pagination">
            <button
                className="pagination-btn"
                disabled={page <= 1}
                onClick={() => onChange(page - 1)}
            >
                <ChevronLeft size={16} /> Prev
            </button>
            <span className="pagination-info">Page {page} of {totalPages}</span>
            <button
                className="pagination-btn"
                disabled={page >= totalPages}
                onClick={() => onChange(page + 1)}
            >
                Next <ChevronRight size={16} />
            </button>
        </div>
    );
};
export default Pagination;
