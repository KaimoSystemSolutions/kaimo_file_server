// Launches a provider's interactive authorization page by POSTing the single-use
// ticket in a form body, so the secret stays out of the URL (and therefore out of
// browser history and reverse-proxy/access logs). The connection id is a plain
// identifier and rides along in the same body for a clean, query-free URL.
window.kaimoPostConnect = function (action, connectionId, ticket) {
    const form = document.createElement('form');
    form.method = 'POST';
    form.action = action;
    const add = (name, value) => {
        const input = document.createElement('input');
        input.type = 'hidden';
        input.name = name;
        input.value = value;
        form.appendChild(input);
    };
    add('connectionId', connectionId);
    add('ticket', ticket);
    document.body.appendChild(form);
    form.submit();
};
