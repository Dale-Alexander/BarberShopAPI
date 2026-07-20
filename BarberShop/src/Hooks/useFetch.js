import { useState, useEffect } from "react";
import axios from "axios";
import { adminAxios } from "./AxiosInterceptor";
const useFetch = (url, isProtected = false) => {
    const [data, setData] = useState();
    const [error, setError] = useState();
    const [loading, setLoading] = useState();
    const instance = isProtected ? adminAxios:axios;
    useEffect(() => {
        if (!url) return;
        const fetchData = async () => {
            setLoading(true);
            setError(undefined); // clear any stale error so a retry that succeeds doesn't stay in the error state
            try {
                const res = await instance.get(url);
                setData(res.data);
            } catch (err) {
                setError(err);
            }
            setLoading(false);
        }
        fetchData();
    }, [url])

    const reFetch = async () => {
        if (!url) return;
        setLoading(true);
        setError(undefined); // same as above - a successful retry must drop the previous error
        try{
            const res = await instance.get(url);
            setData(res.data);
        }
        catch(err){
            setError(err);
        }
        setLoading(false);
    }
    return {data, loading, error, reFetch};
}
export default useFetch;