package com.phonedrive.android.server;

import android.util.Log;
import java.lang.reflect.Field;
import javax.crypto.Cipher;
import javax.crypto.Mac;
import org.apache.sshd.common.session.Session;
import org.apache.sshd.common.session.SessionListener;

/** Metadata only: never inspect keys, payloads, peer addresses or authentication data. */
public final class CryptoDiagnosticsListener implements SessionListener {
    @Override public void sessionEvent(Session session, Event event) {
        if (event != Event.Authenticated) return;
        for (boolean incoming : new boolean[]{true,false}) {
            Object cipher=session.getCipherInformation(incoming);
            Object mac=session.getMacInformation(incoming);
            Log.i("PhoneDriveCrypto", "direction="+(incoming?"inbound":"outbound")+
                " cipher="+(session.getCipherInformation(incoming)==null?"unavailable":session.getCipherInformation(incoming).getTransformation())+
                " cipherProvider="+provider(cipher)+
                " mac="+(session.getMacInformation(incoming)==null?"AEAD/none":session.getMacInformation(incoming).getAlgorithm())+
                " macProvider="+provider(mac));
        }
    }
    private static String provider(Object value) {
        if(value==null)return "none";
        try {
            for(Class<?> type=value.getClass();type!=null;type=type.getSuperclass()) {
                for(Field field:type.getDeclaredFields()) {
                    if(!Cipher.class.isAssignableFrom(field.getType())&&!Mac.class.isAssignableFrom(field.getType()))continue;
                    field.setAccessible(true);
                    Object primitive=field.get(value);
                    if(primitive instanceof Cipher)return ((Cipher)primitive).getProvider().getName();
                    if(primitive instanceof Mac)return ((Mac)primitive).getProvider().getName();
                }
            }
        }catch(ReflectiveOperationException|RuntimeException ignored){}
        return "unavailable";
    }
}
