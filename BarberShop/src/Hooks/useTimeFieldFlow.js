import { useRef } from "react";

/* Keyboard/mouse flow for a group of <input type="time"> fields: close the popup when a value is picked
 * with the mouse, and jump to the next field when one is typed to completion.
 *
 * Shared because two screens need identical behaviour - the shop's opening hours and a barber's shifts -
 * and this took three attempts to get right. A second copy would drift on the fourth.
 *
 * Why it counts KEYSTROKES rather than watching the value:
 *
 *   - a change event only fires when the value actually changes, so retyping "00" over minutes that are
 *     already 00 fires nothing at all. Anything driven off change simply never ran for that entry;
 *   - "the value looks complete" is not the same as "the user has finished". These fields are pre-filled,
 *     so entering just the hour already produces a valid value - the minutes keep their old digits - and
 *     a completeness test fires between the hour and the minute, throwing you out mid-edit.
 *
 * So it models what the browser does with each digit instead. The hour takes two digits, or one when a
 * second cannot follow (3 upwards); the minutes likewise take two, or one from 6 upwards. When the
 * minutes are full, the field is done. Nothing here depends on the value, so it works even when the
 * digits typed happen to match what was there.
 *
 * Clicking straight into the minutes is handled by reading WHERE in the field the click landed - the hour
 * occupies roughly the first third - because starting there means two digits finish the field, not four.
 *
 * Any non-digit key disarms it for that visit. Arrow keys move between segments and Tab leaves entirely,
 * and once either has happened the model no longer knows where the caret is; guessing would be worse than
 * doing nothing, and doing nothing just means the admin tabs as they always have.
 *
 * ASSUMES A 24-HOUR FIELD, which is what the browser renders for this shop's locale. Where Chrome shows a
 * 12-hour clock there is a third AM/PM segment, and completing the minutes would move on one segment too
 * early. Worth knowing before shipping anywhere the locale differs.
 */
export default function useTimeFieldFlow(containerRef) {
    const typed = useRef(false);
    const segment = useRef("hour");
    const hourDigits = useRef(0);
    const minuteDigits = useRef(0);
    const armed = useRef(true);

    const restart = (startingSegment) => {
        segment.current = startingSegment;
        hourDigits.current = 0;
        minuteDigits.current = 0;
        armed.current = true;
    };

    const focusNext = (el) => {
        /* Every enabled time field in the container, in document order - so it runs start -> end -> the
         * next shift on the day -> the next DAY's first field, with none of those transitions special
         * cased. Reading the DOM rather than tracking indices keeps it right if rows are reordered, and
         * days with no fields (the shop is closed) are skipped for free. */
        const fields = containerRef.current
            ? [...containerRef.current.querySelectorAll('input[type="time"]:not([disabled])')]
            : [];
        const next = fields[fields.indexOf(el) + 1];
        if (next) {
            next.focus();
            next.select?.();
        }
    };

    const fieldProps = {
        onFocus: () => {
            typed.current = false;
            restart("hour");
        },
        /* After focus, so it wins - and it also fires when the caret is moved between segments of a field
         * that already has focus, which focus alone would miss. */
        onClick: (e) => {
            const box = e.currentTarget.getBoundingClientRect();
            restart(e.clientX - box.left > box.width * 0.34 ? "minute" : "hour");
        },
        onKeyDown: (e) => {
            typed.current = true;
            if (!/^[0-9]$/.test(e.key)) {
                // Shift alone is a modifier, not navigation - it must not disarm a shifted digit.
                if (e.key !== "Shift") armed.current = false;
                return;
            }
            if (!armed.current) return;

            const digit = Number(e.key);
            if (segment.current === "hour") {
                hourDigits.current += 1;
                // 3-9 can't take a second digit, so the browser moves on immediately.
                if (hourDigits.current >= 2 || digit >= 3) {
                    segment.current = "minute";
                    minuteDigits.current = 0;
                }
                return;
            }

            minuteDigits.current += 1;
            if (minuteDigits.current >= 2 || digit >= 6) {
                const el = e.currentTarget;
                // A frame's grace so the browser commits the digit before focus moves off the field.
                requestAnimationFrame(() => focusNext(el));
            }
        },
    };

    /* Pair with the field's own onChange. Only handles the mouse case: a value chosen from the popup
     * raises no keydown, so this closes the popup instead of advancing. */
    const onValueChange = (e) => {
        if (typed.current) return;
        const el = e.target;
        requestAnimationFrame(() => el.blur());
    };

    return { fieldProps, onValueChange };
}
