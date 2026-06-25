// WebAuthn passkey ceremonies for the admin UI. Talks to the /account/passkey/* endpoints.
// Credentials are serialized manually (not JSON.stringify(credential)) because some password managers
// implement PublicKeyCredential.toJSON incorrectly ("Illegal invocation"); see the .NET 10 passkey docs.
(function () {
    function b64url(o) {
        if (o === null || o === undefined) return undefined;
        if (Array.isArray(o)) o = Uint8Array.from(o);
        if (o instanceof ArrayBuffer) o = new Uint8Array(o);
        if (o instanceof Uint8Array) {
            let s = '';
            for (let i = 0; i < o.byteLength; i++) s += String.fromCharCode(o[i]);
            o = window.btoa(s);
        }
        if (typeof o !== 'string') throw new Error('Could not convert to base64');
        return o.replace(/\+/g, '-').replace(/\//g, '_').replace(/=*$/g, '');
    }

    function serialize(c) {
        const r = c.response;
        return JSON.stringify({
            authenticatorAttachment: c.authenticatorAttachment,
            clientExtensionResults: c.getClientExtensionResults ? c.getClientExtensionResults() : {},
            id: c.id,
            rawId: b64url(c.rawId),
            type: c.type,
            response: {
                attestationObject: b64url(r.attestationObject),
                authenticatorData: b64url(r.authenticatorData ?? (r.getAuthenticatorData ? r.getAuthenticatorData() : undefined)),
                clientDataJSON: b64url(r.clientDataJSON),
                publicKey: b64url(r.getPublicKey ? r.getPublicKey() : undefined),
                publicKeyAlgorithm: r.getPublicKeyAlgorithm ? r.getPublicKeyAlgorithm() : undefined,
                transports: r.getTransports ? r.getTransports() : undefined,
                signature: b64url(r.signature),
                userHandle: b64url(r.userHandle),
            },
        });
    }

    function post(url, body) {
        return fetch(url, {
            method: 'POST',
            headers: body ? { 'Content-Type': 'application/json' } : undefined,
            body: body,
        });
    }

    function setStatus(id, msg) {
        const el = id && document.getElementById(id);
        if (el) el.textContent = msg;
    }

    async function register(optionsUrl, registerUrl, statusId) {
        try {
            const optResp = await post(optionsUrl);
            if (!optResp.ok) throw new Error('Could not start registration (' + optResp.status + ')');
            const options = PublicKeyCredential.parseCreationOptionsFromJSON(await optResp.json());
            const credential = await navigator.credentials.create({ publicKey: options });
            const resp = await post(registerUrl, serialize(credential));
            if (!resp.ok) throw new Error(await resp.text());
            location.reload();
        } catch (e) {
            setStatus(statusId, 'Could not add passkey: ' + (e && e.message ? e.message : e));
        }
    }

    async function signin(requestUrl, signinUrl, redirectUrl, statusId) {
        try {
            const optResp = await post(requestUrl);
            if (!optResp.ok) throw new Error('Could not start sign-in (' + optResp.status + ')');
            const options = PublicKeyCredential.parseRequestOptionsFromJSON(await optResp.json());
            const credential = await navigator.credentials.get({ publicKey: options });
            const resp = await post(signinUrl, serialize(credential));
            if (!resp.ok) throw new Error('Sign-in failed');
            location.href = redirectUrl || '/';
        } catch (e) {
            setStatus(statusId, 'Passkey sign-in failed: ' + (e && e.message ? e.message : e));
        }
    }

    window.winSmtpPasskey = { register: register, signin: signin };
})();
