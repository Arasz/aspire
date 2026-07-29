export function togglePasswordVisibility(inputId) {
    const input = document.getElementById(inputId);
    if (input) {
        const currentType = input.getAttribute('type');
        const newType = currentType === 'password' ? 'text' : 'password';
        input.setAttribute('type', newType);
    }
}

// Edge and other Chromium browsers render their own password reveal button (::-ms-reveal) and
// clear button (::-ms-clear) inside <input type=password>. The secret-text inputs in this dialog
// already provide a custom show/hide "eye" toggle (see the SecretText case in
// InteractionsInputDialog.razor), so the native control is a confusing duplicate icon. Those
// pseudo-elements live inside the FluentTextField's shadow DOM, which a normal document-level
// stylesheet cannot reach (::part(control)::-ms-reveal does not parse, and plain input::-ms-reveal
// cannot pierce the shadow boundary). Injecting a small scoped stylesheet into the field's own
// shadowRoot is the only reliable way to hide it.
//
// Returns true once the style is present so the caller can stop retrying; returns false when the
// host or its shadow root is not ready yet. Idempotent: repeated calls won't stack <style> nodes.
export function hideNativeReveal(hostId) {
    const host = document.getElementById(hostId);
    if (!host || !host.shadowRoot) {
        return false;
    }

    const styleId = 'aspire-hide-native-reveal';
    if (!host.shadowRoot.getElementById(styleId)) {
        const style = document.createElement('style');
        style.id = styleId;
        style.textContent = 'input::-ms-reveal, input::-ms-clear { display: none; }';
        host.shadowRoot.appendChild(style);
    }

    return true;
}
