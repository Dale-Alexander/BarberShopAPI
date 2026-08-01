/* Phone numbers are stored in full international form (+35679123456) because that's what the checkout's
   FancyPhoneInput produces and what any SMS/dialling integration would need. Staff screens are a
   different audience: the shop is in Malta, so "+356" on every row is noise that pushes the digits
   people actually read further right.

   So: strip the local dialling code, keep it on anything foreign - a tourist's +44 number is exactly
   when you need to see the country. Formatting only; the stored value is never rewritten. */
const LOCAL_DIALLING_CODE = "+356";

export const formatPhone = (phone) => {
    if (!phone) return phone;
    const trimmed = String(phone).trim();
    if (!trimmed.startsWith(LOCAL_DIALLING_CODE)) return trimmed;
    // Numbers are sometimes stored with a space after the code ("+356 7912 3456").
    const local = trimmed.slice(LOCAL_DIALLING_CODE.length).trim();
    // A bare "+356" with nothing after it isn't a number - show what's there rather than an empty cell.
    return local || trimmed;
};
