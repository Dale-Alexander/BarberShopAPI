import { useState, useRef, useEffect } from "react";
import { parseCountry, defaultCountries } from "react-international-phone";
import "./FancyPhoneInput.css";

const FancyPhoneInput = ({ value, onChange }) => {
    const [open, setOpen] = useState(false);
    const [search, setSearch] = useState("");
    const inputRef = useRef(null);
    const dropdownRef = useRef(null);
    const searchRef = useRef(null);

    const countries = defaultCountries.map(parseCountry);

    const initCountry = () => {
        if (!value) return { name: "Malta", iso2: "mt", dialCode: "356" };
        const match = countries.find(c => value.startsWith(`+${c.dialCode}`));
        return match ?? { name: "Malta", iso2: "mt", dialCode: "356" };
    };

    const initNumber = () => {
        if (!value) return "";
        const match = countries.find(c => value.startsWith(`+${c.dialCode}`));
        return match ? value.slice(match.dialCode.length + 2).trim() : "";
    };

    const [selected, setSelected] = useState(initCountry);
    const [numberPart, setNumberPart] = useState(initNumber);

    const filtered = countries.filter(c =>
        c.name.toLowerCase().includes(search.toLowerCase()) ||
        c.dialCode.includes(search.replace("+", ""))
    );

    useEffect(() => {
        const handler = (e) => {
            if (dropdownRef.current && !dropdownRef.current.contains(e.target)) {
                setOpen(false);
                setSearch("");
            }
        };
        document.addEventListener("mousedown", handler);
        return () => document.removeEventListener("mousedown", handler);
    }, []);

    useEffect(() => {
        if (open && searchRef.current) searchRef.current.focus();
    }, [open]);

    const selectCountry = (country) => {
        setSelected(country);
        setOpen(false);
        setSearch("");
        onChange(`+${country.dialCode} ${numberPart}`, country.iso2.toUpperCase());
        setTimeout(() => inputRef.current?.focus(), 0);
    };

    const handleNumber = (e) => {
        const val = e.target.value.replace(/[^\d\s\-().]/g, "");
        setNumberPart(val);
        onChange(`+${selected.dialCode} ${val}`, selected.iso2.toUpperCase());
    };

    const getFlagEmoji = (iso2) => {
        return iso2.toUpperCase().replace(/./g, ch =>
            String.fromCodePoint(0x1F1E6 - 65 + ch.charCodeAt(0))
        );
    };

    return (
        <div className="fpi-wrapper" ref={dropdownRef}>
            <div className={`fpi-input-row ${open ? "fpi-input-row--open" : ""}`}>
                {/* Flag button */}
                <button
                    type="button"
                    className="fpi-flag-btn"
                    onClick={() => setOpen(o => !o)}
                    aria-label="Select country"
                >
                    <span className="fpi-flag">{getFlagEmoji(selected.iso2)}</span>
                    <span className="fpi-dialcode">+{selected.dialCode}</span>
                    <svg className={`fpi-chevron ${open ? "fpi-chevron--up" : ""}`} width="12" height="12" viewBox="0 0 12 12">
                        <path d="M2 4l4 4 4-4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" fill="none" />
                    </svg>
                </button>

                <div className="fpi-divider" />

                {/* Number input */}
                <input
                    ref={inputRef}
                    type="tel"
                    className="fpi-number-input"
                    placeholder="Enter phone number"
                    value={numberPart}
                    onChange={handleNumber}
                    name="phone"
                    required
                />
            </div>

            {/* Dropdown */}
            {open && (
                <div className="fpi-dropdown">
                    <div className="fpi-search-wrap">
                        <svg className="fpi-search-icon" width="14" height="14" viewBox="0 0 14 14" fill="none">
                            <circle cx="6" cy="6" r="4.5" stroke="#bbb" strokeWidth="1.4" />
                            <path d="M9.5 9.5l2.5 2.5" stroke="#bbb" strokeWidth="1.4" strokeLinecap="round" />
                        </svg>
                        <input
                            ref={searchRef}
                            className="fpi-search"
                            placeholder="Search country..."
                            value={search}
                            onChange={e => setSearch(e.target.value)}
                        />
                        {search && (
                            <button className="fpi-search-clear" onClick={() => setSearch("")}>×</button>
                        )}
                    </div>

                    <ul className="fpi-list">
                        {filtered.length === 0 && (
                            <li className="fpi-empty">No countries found</li>
                        )}
                        {filtered.map(c => (
                            <li
                                key={c.iso2}
                                className={`fpi-item ${selected.iso2 === c.iso2 ? "fpi-item--active" : ""}`}
                                onClick={() => selectCountry(c)}
                            >
                                <span className="fpi-item-flag">{getFlagEmoji(c.iso2)}</span>
                                <span className="fpi-item-name">{c.name}</span>
                                <span className="fpi-item-code">+{c.dialCode}</span>
                                {selected.iso2 === c.iso2 && (
                                    <svg className="fpi-check" width="13" height="13" viewBox="0 0 13 13">
                                        <path d="M2 6.5l3.5 3.5 5.5-6" stroke="#c9a84c" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" fill="none" />
                                    </svg>
                                )}
                            </li>
                        ))}
                    </ul>
                </div>
            )}
        </div>
    );
};

export default FancyPhoneInput;