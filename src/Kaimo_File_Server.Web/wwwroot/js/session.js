// Web session cookie handoff (see WebSessionController). The circuit cannot set cookies, so it
// asks the page to post a one-time ticket to /auth/session; the response sets the HttpOnly
// session cookie. The token itself never reaches script.
window.kaimoSession = {
    post: async function (url, body) {
        try {
            const response = await fetch(url, {
                method: 'POST',
                credentials: 'same-origin',
                cache: 'no-store',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body || {})
            });
            return response.ok;
        } catch (e) {
            return false;
        }
    }
};

// The session token used to live in localStorage; remove leftovers of older versions.
try { localStorage.removeItem('auth_token'); } catch (e) { }
