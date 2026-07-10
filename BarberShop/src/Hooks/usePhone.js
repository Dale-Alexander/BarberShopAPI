import { useState } from "react";
import { isValidPhoneNumber } from "libphonenumber-js/max";

const usePhone = (phone) => {
    const [phoneCountry, setPhoneCountry] = useState("MT");
    const handlePhoneChange = (val, iso2, setPhone) => {
        setPhone(val);
        setPhoneCountry(iso2);
    };

    const isValid = () => {
        if (!phone) return false;
        return isValidPhoneNumber(phone, phoneCountry);
    };

    return { phoneCountry, handlePhoneChange, isValid };
};

export default usePhone;