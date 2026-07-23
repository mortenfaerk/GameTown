// Game archive upload with real progress.
//
// Blazor WASM's HttpClient goes through the browser's fetch(), which has no upload progress events —
// so a genuine progress bar is not possible from C#. XMLHttpRequest does expose upload.onprogress,
// hence this module.
//
// It also avoids a second problem: reading the file through IBrowserFile.OpenReadStream pulls the
// whole archive into the WASM heap before sending, which falls over on large files. Here the
// browser's File object goes straight to the network stack and never enters .NET memory.

// One upload at a time is all the UI offers, so a module-level handle is enough for cancellation.
let currentRequest = null;

/**
 * @param {HTMLInputElement} inputElement the <input type="file"> rendered by InputFile
 * @param {string} url                    absolute URL of the upload endpoint
 * @param {object} fields                 text form fields; keys must match the API's [FromForm(Name)]
 * @param {object} dotNetRef              receives OnUploadProgress(loaded, total)
 * @returns {Promise<{status: number, body: string, aborted: boolean}>}
 */
export function uploadGame(inputElement, url, fields, dotNetRef) {
    return new Promise((resolve, reject) => {
        const file = inputElement?.files?.[0];
        if (!file) {
            reject(new Error("No file was selected."));
            return;
        }

        const form = new FormData();
        for (const [key, value] of Object.entries(fields ?? {})) {
            if (value !== null && value !== undefined && value !== "") {
                form.append(key, value);
            }
        }
        form.append("file", file, file.name);

        const xhr = new XMLHttpRequest();
        currentRequest = xhr;

        xhr.open("POST", url, true);
        // No Authorization header: authentication is a same-origin cookie, which the browser
        // attaches to this XHR by itself. withCredentials is not needed for same-origin either,
        // and setting it would be a no-op that implies a cross-origin design we no longer have.

        xhr.upload.onprogress = (e) => {
            // Not computable for chunked encoding; the UI falls back to indeterminate.
            if (e.lengthComputable) {
                dotNetRef.invokeMethodAsync("OnUploadProgress", e.loaded, e.total);
            }
        };

        xhr.onload = () => {
            currentRequest = null;
            resolve({ status: xhr.status, body: xhr.responseText ?? "", aborted: false });
        };

        xhr.onerror = () => {
            currentRequest = null;
            // Status is unavailable on a transport failure; the caller reports it as a network error.
            reject(new Error("The upload could not reach the server."));
        };

        xhr.onabort = () => {
            currentRequest = null;
            resolve({ status: 0, body: "", aborted: true });
        };

        xhr.send(form);
    });
}

/** Cancels an upload in flight. Safe to call when nothing is running. */
export function abortUpload() {
    if (currentRequest) {
        currentRequest.abort();
        currentRequest = null;
    }
}
