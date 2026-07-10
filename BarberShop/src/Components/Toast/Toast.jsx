import {useState, useEffect, useRef} from "react";
import "./Toast.css";

const Toast = ({title, message, duration = 5000, onClose}) =>{
    const [visible, setVisible] = useState(true);
    const [dismissing, setDismissing] = useState(false);
    const timerRef = useRef(null);

    const dismiss = () => {
        if (dismissing) return;
        //the user can click close exactly when the timer finishes. 
        //Without this youd kick off two competing setTmeout(..., 300)
        setDismissing(true);//flips the toast into its "exit" visual state
        //the CSS transition kicks in
        setTimeout(() => {
            setVisible(false);
            onClose?.();
        }, 300);

        /* wait for the 300ms exit animation to finish, then removes the toast from
        the DOM by setting visible to false which causes the component to return null.
        Without the timeout, the toast would disappear without the animation*/

        /* onClose is hideToast passed from the context. You might be wondering
        why we need onClose since we are removing the toast from the dom
        in the toast component.Without onClose, the toast visually 
        disappears(returns null) but the provider's toast state is never
        reset to null. This means if you call showToast again with the same arguments,
        react sees no state change and wont re-render. So the new toast
        never appears. Thats how react state works, it compares the old version to the new version*/
    }
    useEffect(() => {
        timerRef.current = setTimeout(dismiss, duration);
        /* we set a timer because when the user doesnt close the modal himself,
        the modal auto closes. When the user clicks close:
        mount timer starts(5000ms) -> user clicks at 2000ms -> dismiss() runs
        -> exit animation -> remvoed from DOM -> timer firest at 4500 -> clearTimeout already cancelled it.
        But if the user ddidnt close it , the timer will run and autocloses the toast*/
        return () => clearTimeout(timerRef.current);
    }, []);

    if (!visible) return null;

return(
    <div style={{ position: "fixed", top: 20, right: 20, zIndex: 9999 }}>
        <div
            style={{
                position: "relative",
                overflow: "hidden",
                borderRadius: 12,
                background: "#1a1a1a",
                border: "1px solid rgba(229, 62, 62, 0.25)",
                minWidth: 320,
                maxWidth: 400,
                opacity: dismissing ? 0 : 1,
                transform: dismissing ? "translateX(60px)" : "translateX(0)",
                transition: dismissing
                    ? "opacity 0.3s ease, transform 0.3s ease"
                    : "opacity 0.35s cubic-bezier(0.34,1.56,0.64,1), transform 0.35s cubic-bezier(0.34,1.56,0.64,1)",
            }}
        >
            {/* Accent bar */}
            <div
                style={{
                    position: "absolute",
                    left: 0, top: 0, bottom: 0,
                    width: 4,
                    borderRadius: "12px 0 0 12px",
                    background: "#e53e3e",
                }}
            />

            {/* Body */}
            <div style={{ display: "flex", alignItems: "flex-start", gap: 12, padding: "16px 16px 16px 20px" }}>
                <svg style={{ flexShrink: 0, width: 18, height: 18, marginTop: 2, color: "#e53e3e" }} viewBox="0 0 20 20" fill="currentColor">
                    <path fillRule="evenodd" d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zm-7 4a1 1 0 11-2 0 1 1 0 012 0zm-1-9a1 1 0 00-1 1v4a1 1 0 102 0V6a1 1 0 00-1-1z" clipRule="evenodd" />
                </svg>

                <div style={{ flex: 1, minWidth: 0 }}>
                    <p style={{ margin: 0, fontSize: 14, fontWeight: 600, color: "#fff" }}>{title}</p>
                    <p style={{ margin: "4px 0 0", fontSize: 12, lineHeight: 1.5, color: "#9a9a9a" }}>{message}</p>
                </div>

                <button
                    onClick={dismiss}
                    style={{
                        flexShrink: 0,
                        width: 20, height: 20,
                        display: "flex", alignItems: "center", justifyContent: "center",
                        background: "none", border: "none", borderRadius: 4,
                        cursor: "pointer", color: "#666",
                    }}
                >
                    <svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round">
                        <path d="M1 1l10 10M11 1L1 11" />
                    </svg>
                </button>
            </div>

            {/* Progress bar */}
            <div style={{ position: "absolute", bottom: 0, left: 4, right: 0, height: 2, background: "rgba(255,255,255,0.05)" }}>
                <div
                    style={{
                        height: "100%",
                        borderRadius: 9999,
                        background: "rgba(229,62,62,0.6)",
                        animation: `shrink ${duration}ms linear forwards`,
                    }}
                />
            </div>

            <style>{`@keyframes shrink { from { width: 100% } to { width: 0% } }`}</style>
        </div>
    </div>
)
}

export default Toast;