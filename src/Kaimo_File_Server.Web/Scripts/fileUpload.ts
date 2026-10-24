// Folder-aware uploads. Blazor's InputFile only reports each file's leaf name, so the
// folder of every file is registered with .NET first (SetPendingUploadPaths, awaited)
// and only then are the files handed to the Blazor input. dirs[i] belongs to files[i].
window.kaimoUpload = (() => {
    const MAX_FILES = 5000;

    // Program.cs caps one SignalR message at 1 MB (MaximumReceiveMessageSize). InputFile sends
    // the metadata of ALL selected files in one message, and SetPendingUploadPaths the folders;
    // an oversized message drops the whole circuit, so such a selection is refused up front.
    const MAX_MESSAGE_BYTES = 1024 * 1024 - 64 * 1024; // headroom for the envelope
    const utf8 = new TextEncoder();
    // Arguments travel as a JSON string; stringifying twice also covers the escaping of the
    // JSON hub protocol, so this is an upper bound for either protocol.
    const messageBytes = (args: unknown[]) => utf8.encode(JSON.stringify(JSON.stringify(args))).length;

    function fitsInMessages(files: File[], packed: { dirs: string[], dirIndex: number[] }, emptyDirs: string[]) {
        const meta = files.map(f => ({
            id: 1e9, lastModified: new Date(f.lastModified).toISOString(),
            name: f.name, size: f.size, contentType: f.type,
        }));
        return messageBytes([meta]) <= MAX_MESSAGE_BYTES
            && messageBytes([packed.dirs, packed.dirIndex, emptyDirs]) <= MAX_MESSAGE_BYTES;
    }

    // Every distinct folder once plus one index per file (UploadFolderPath.Unpack).
    function packDirs(dirs: string[]) {
        const unique: string[] = [];
        const indexOf = new Map<string, number>();
        const dirIndex = dirs.map(dir => {
            if (!indexOf.has(dir)) {
                indexOf.set(dir, unique.length);
                unique.push(dir);
            }
            return indexOf.get(dir)!;
        });
        return { dirs: unique, dirIndex };
    }

    const readEntries = (reader: FileSystemDirectoryReader) => new Promise<FileSystemEntry[]>((resolve, reject) => reader.readEntries(resolve, reject));
    const entryFile = (entry: FileSystemFileEntry) => new Promise<File>((resolve, reject) => entry.file(resolve, reject));
    const trimSlashes = (path: string | null | undefined) => (path || '').replace(/^\/+|\/+$/g, '');

    // "/Photos/2024/a.jpg" or "Photos/2024/a.jpg" -> "Photos/2024"
    function dirOf(path: string) {
        const p = trimSlashes(path);
        const i = p.lastIndexOf('/');
        return i < 0 ? '' : p.slice(0, i);
    }

    // Must run synchronously inside the drop event: DataTransfer items are only
    // readable while the event is being dispatched.
    function snapshotDrop(dataTransfer: DataTransfer): KaimoDropSnapshot {
        const items = Array.from(dataTransfer.items || []).filter(i => i.kind === 'file');
        if (items.length === 0 || typeof items[0].webkitGetAsEntry !== 'function')
            return { entries: [], loose: Array.from(dataTransfer.files || []) };

        const entries: FileSystemEntry[] = [];
        const loose: File[] = [];
        for (const item of items) {
            const entry = item.webkitGetAsEntry();
            if (entry) {
                entries.push(entry);
            } else {
                const file = item.getAsFile();
                if (file) loose.push(file);
            }
        }
        return { entries, loose };
    }

    // Walks dropped folders recursively. Stops early once more than maxFiles were found.
    async function collect(snapshot: KaimoDropSnapshot, maxFiles = MAX_FILES): Promise<KaimoCollectedFiles> {
        const files = [...snapshot.loose];
        const dirs = files.map(() => '');
        const emptyDirs: string[] = [];

        async function walk(entry: FileSystemEntry) {
            if (files.length > maxFiles) return;
            if (entry.isFile) {
                try {
                    files.push(await entryFile(entry as FileSystemFileEntry));
                    dirs.push(dirOf(entry.fullPath));
                } catch (err) {
                    console.warn('Skipping unreadable file', entry.fullPath, err);
                }
                return;
            }

            // readEntries returns at most ~100 entries per call; read until it is drained.
            const reader = (entry as FileSystemDirectoryEntry).createReader();
            let hasChildren = false;
            for (;;) {
                let batch: FileSystemEntry[];
                try {
                    batch = await readEntries(reader);
                } catch (err) {
                    // Like an unreadable file: skip this folder, keep the rest of the drop.
                    console.warn('Skipping unreadable folder', entry.fullPath, err);
                    return;
                }
                if (batch.length === 0) break;
                hasChildren = true;
                for (const child of batch) await walk(child);
            }
            if (!hasChildren) emptyDirs.push(trimSlashes(entry.fullPath));
        }

        for (const entry of snapshot.entries) await walk(entry);
        return { files, dirs, emptyDirs };
    }

    async function handOver(inputId: string, dotNetRef: DotNetObjectReference, collected: KaimoCollectedFiles, maxFiles = MAX_FILES) {
        const { files, dirs, emptyDirs } = collected;
        if (files.length > maxFiles) {
            await dotNetRef.invokeMethodAsync('OnUploadTooManyFiles', maxFiles);
            return;
        }
        if (files.length === 0 && emptyDirs.length === 0) return;

        const packed = packDirs(dirs);
        if (!fitsInMessages(files, packed, emptyDirs)) {
            await dotNetRef.invokeMethodAsync('OnUploadSelectionTooLarge');
            return;
        }

        await dotNetRef.invokeMethodAsync('SetPendingUploadPaths', packed.dirs, packed.dirIndex, emptyDirs);

        // Look the input up only now: the active upload slot may have changed meanwhile.
        const input = document.getElementById(inputId) as HTMLInputElement | null;
        if (!input) {
            // Withdraw the folders so they cannot attach to a later selection.
            await dotNetRef.invokeMethodAsync('SetPendingUploadPaths', [], [], []);
            return;
        }
        const transfer = new DataTransfer();
        files.forEach(f => transfer.items.add(f));
        input.files = transfer.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
    }

    async function handleDrop(dataTransferSnapshot: KaimoDropSnapshot, inputId: string, dotNetRef: DotNetObjectReference, maxFiles = MAX_FILES) {
        try {
            const collected = await collect(dataTransferSnapshot, maxFiles);
            await handOver(inputId, dotNetRef, collected, maxFiles);
        } catch (err) {
            console.error('Folder upload failed', err);
        }
    }

    // Opens the browser's file dialog of the layout's active hidden InputFile.
    function openFilePicker(inputId = 'global-file-input') {
        document.getElementById(inputId)?.click();
    }

    // Opens the browser's folder dialog. Files keep their folder in webkitRelativePath.
    function pickFolder(dotNetRef: DotNetObjectReference, inputId = 'global-file-input', maxFiles = MAX_FILES) {
        const picker = document.createElement('input');
        picker.type = 'file';
        picker.multiple = true;
        picker.webkitdirectory = true;
        picker.style.display = 'none';
        picker.addEventListener('change', async () => {
            const files = Array.from(picker.files || []);
            picker.remove();
            try {
                await handOver(inputId, dotNetRef,
                    { files, dirs: files.map(f => dirOf(f.webkitRelativePath)), emptyDirs: [] },
                    maxFiles);
            } catch (err) {
                console.error('Folder upload failed', err);
            }
        }, { once: true });
        picker.addEventListener('cancel', () => picker.remove(), { once: true });
        document.body.appendChild(picker);
        picker.click();
    }

    // Public upload link page: the drop label wraps its own InputFile.
    function initPublicDrop(dropSelector: string, inputId: string, dotNetRef: DotNetObjectReference, maxFiles: number) {
        const zone = document.querySelector<HTMLElement>(dropSelector);
        if (!zone || zone.dataset.kaimoFolderDrop) return;
        zone.dataset.kaimoFolderDrop = '1';

        const isFileDrag = (e: DragEvent) => !!e.dataTransfer && Array.from(e.dataTransfer.types).includes('Files');
        zone.addEventListener('dragover', e => {
            if (!isFileDrag(e)) return;
            e.preventDefault();
            zone.classList.add('public-upload-drop--over');
        });
        zone.addEventListener('dragleave', e => {
            if (!zone.contains(e.relatedTarget as Node | null)) zone.classList.remove('public-upload-drop--over');
        });
        zone.addEventListener('drop', e => {
            if (!isFileDrag(e)) return;
            // Take over from the native input so dropped folders keep their structure.
            e.preventDefault();
            zone.classList.remove('public-upload-drop--over');
            if ((document.getElementById(inputId) as HTMLInputElement | null)?.disabled) return;
            handleDrop(snapshotDrop(e.dataTransfer!), inputId, dotNetRef, maxFiles);
        });
    }

    return { MAX_FILES, snapshotDrop, handleDrop, openFilePicker, pickFolder, initPublicDrop };
})();

function initFileUpload(elementSelector: string, dotNetRef: DotNetObjectReference | null) {

    // FileBrowser can be destroyed and recreated while the layout (and its file
    // input) stays alive. Abort the previous document-level handlers so one OS drop
    // can never dispatch several concurrent uploads after navigating around.
    window.kaimoFileUploadAbortController?.abort();
    const controller = new AbortController();
    window.kaimoFileUploadAbortController = controller;
    const listenerOptions = { signal: controller.signal };

    function isOsFileDrag(e: DragEvent) {
        return !!e.dataTransfer && Array.from(e.dataTransfer.types).includes('Files');
    }

    document.addEventListener('dragenter', e => {
        if (!isOsFileDrag(e)) return;

        const el = document.querySelector(elementSelector);
        e.preventDefault();
        if (el?.contains(e.target as Node | null)) {
            el.classList.add('file-dragged-over');
        }
    }, listenerOptions);

    document.addEventListener("dragstart", e => {
        e.dataTransfer!.setData("text/html", "...")
    }, listenerOptions)

    document.addEventListener('dragover', e => {
        if (!isOsFileDrag(e)) return;

        const el = document.querySelector(elementSelector);
        e.preventDefault();

        if (el?.contains(e.target as Node | null)) {
            el.classList.add('file-dragged-over');
        }
    }, listenerOptions);

    document.addEventListener('dragleave', e => {
        if (!isOsFileDrag(e)) return;

        const el = document.querySelector(elementSelector);
        if (el && !el.contains(e.relatedTarget as Node | null)) {
            el.classList.remove('file-dragged-over');
        }
    }, listenerOptions);

    document.addEventListener('drop', e => {
        if (!isOsFileDrag(e)) return;

        e.preventDefault();
        const el = document.querySelector(elementSelector);
        if (el?.contains(e.target as Node | null)) {
            el.classList.remove('file-dragged-over');
            if (dotNetRef) {
                window.kaimoUpload.handleDrop(
                    window.kaimoUpload.snapshotDrop(e.dataTransfer!), 'global-file-input', dotNetRef);
                return;
            }
            const input = document.getElementById('global-file-input') as HTMLInputElement | null;
            if (!input) return;
            input.files = e.dataTransfer!.files;
            input.dispatchEvent(new Event('change', { bubbles: true }));
        }
    }, listenerOptions);
}
