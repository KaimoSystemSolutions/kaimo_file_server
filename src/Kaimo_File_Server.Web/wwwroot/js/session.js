"use strict";
var __awaiter = (this && this.__awaiter) || function (thisArg, _arguments, P, generator) {
    function adopt(value) { return value instanceof P ? value : new P(function (resolve) { resolve(value); }); }
    return new (P || (P = Promise))(function (resolve, reject) {
        function fulfilled(value) { try { step(generator.next(value)); } catch (e) { reject(e); } }
        function rejected(value) { try { step(generator["throw"](value)); } catch (e) { reject(e); } }
        function step(result) { result.done ? resolve(result.value) : adopt(result.value).then(fulfilled, rejected); }
        step((generator = generator.apply(thisArg, _arguments || [])).next());
    });
};
// Web session cookie handoff (see WebSessionController). The circuit cannot set cookies, so it
// asks the page to post a one-time ticket to /auth/session; the response sets the HttpOnly
// session cookie. The token itself never reaches script.
window.kaimoSession = {
    post: function (url, body) {
        return __awaiter(this, void 0, void 0, function* () {
            try {
                const response = yield fetch(url, {
                    method: 'POST',
                    credentials: 'same-origin',
                    cache: 'no-store',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(body || {})
                });
                return response.ok;
            }
            catch (e) {
                return false;
            }
        });
    }
};
// The session token used to live in localStorage; remove leftovers of older versions.
try {
    localStorage.removeItem('auth_token');
}
catch (e) { }
