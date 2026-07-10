import {createContext, useState, useEffect} from "react";
import axios from "axios";
export const AuthContext = createContext();

export const AuthContextProvider = ({children}) =>{
const [loading, setLoading] = useState(true);
const [user, setUser] = useState();
useEffect(()=>{
const fetchUser = async() =>{
    try{
        const res = await axios.get("/api/auth/me", {withCredentials:true});
        /* withCredentials:true tells the browser to send the cookie to the server. The server needs it:
        context.Token = context.Request.Cookies["jwt"] from this, the middleware internally extracts the claims and sets
        HttpContext.User to some object involving those claims */
        setUser(res.data);
    }
    catch(err){
        setUser(null);
        console.log(err);
    }
    finally{
        setLoading(false);
    }
}
fetchUser();
}, [])
return(
<AuthContext.Provider value = {{user, setUser, loading, setLoading}}>
    {children}
</AuthContext.Provider>
);
}