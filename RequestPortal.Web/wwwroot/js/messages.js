(() => {
    const badge = document.getElementById("messageUnreadBadge");
    const toast = document.getElementById("messageToast");
    const toastTitle = document.getElementById("messageToastTitle");
    const toastBody = document.getElementById("messageToastBody");
    const toastLink = document.getElementById("messageToastLink");

    async function refreshUnread() {
        if (!badge) return;
        try {
            const response = await fetch("/api/messages/unread-count", {
                credentials: "same-origin",
                headers: { "Accept": "application/json" }
            });
            if (!response.ok) return;
            const payload = await response.json();
            const count = Number(payload.unread || 0);
            badge.textContent = String(count);
            badge.hidden = count <= 0;
        } catch {
            // Mailbox availability must not affect normal page use.
        }
    }

    function showToast(message) {
        if (!toast) return;
        toastTitle.textContent = message.senderName
            ? `New message from ${message.senderName}`
            : "New message";
        toastBody.textContent = message.preview || message.subject || "";
        toastLink.href = message.threadId
            ? `/Messages/Thread/${message.threadId}`
            : "/Messages/Inbox";
        toast.hidden = false;

        window.clearTimeout(showToast.timeout);
        showToast.timeout = window.setTimeout(() => {
            toast.hidden = true;
        }, 8000);
    }

    refreshUnread();

    if (!window.signalR) {
        window.setInterval(refreshUnread, 15000);
        return;
    }

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/messages")
        .withAutomaticReconnect()
        .build();

    connection.on("messageReceived", message => {
        showToast(message);
        refreshUnread();
    });

    connection.start().catch(() => {
        window.setInterval(refreshUnread, 15000);
    });
})();
