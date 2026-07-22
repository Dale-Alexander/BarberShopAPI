// Central place that turns a caught request error into a user-facing message that always tells the
// user what to do next. Order of preference:
//   1. Request sent but no response came back  -> offline / network / CORS / timeout message.
//   2. The backend sent its own message         -> show that (already user-facing, per API convention).
//   3. Nothing usable                           -> the caller's contextual fallback, which must itself
//                                                   end with a next step (e.g. "... Please try again.").
export const getErrorMessage = (err, fallback = "Something went wrong. Please try again.") => {
    if (err?.request && !err?.response) {
        return "Couldn't reach the server. Check your connection and try again.";
    }
    return err?.response?.data?.message || fallback;
};
