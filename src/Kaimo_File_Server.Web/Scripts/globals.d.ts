// Window globals exposed by the scripts in this folder and called from C# via IJSRuntime
// ("kaimoSession.post" etc.). Extend this interface when a script is migrated to TypeScript.
interface Window {
    kaimoSession: {
        post(url: string, body?: unknown): Promise<boolean>;
    };
    kaimoPostConnect(action: string, connectionId: string, ticket: string): void;
    mailTemplateEditor: {
        insertAtCursor(element: HTMLTextAreaElement | null, text: string): void;
    };
    kaimoGlobalSearch: {
        revealActive(): void;
    };
    contextMenu: {
        reposition(el: HTMLElement | null): Promise<void> | undefined;
    };
    infoPopover: {
        MARGIN: number;
        GAP: number;
        place(panel: HTMLElement | null, anchor: Element | null): Promise<void> | undefined;
        _position(panel: HTMLElement, anchor: Element): void;
    };
    kaimoAvatarCrop: {
        init(canvas: KaimoCropCanvas, dataUrl: string, size: number): Promise<boolean>;
        zoom(canvas: KaimoCropCanvas | null, factor: number): void;
        result(canvas: HTMLCanvasElement): string;
    };
    kaimoInternalDragDropAbortController?: AbortController;
    filePreview: {
        preparePreview(bytes: Uint8Array, contentType: string | null, kind: string)
            : Promise<{ displayUrl: string | null; downloadUrl: string }>;
        revokeBlob(url: string | null): void;
        downloadBytes(bytes: Uint8Array, contentType: string | null, fileName: string | null): void;
        downloadFromUrl(url: string, fileName: string | null): void;
    };
    kaimoUpload: {
        MAX_FILES: number;
        snapshotDrop(dataTransfer: DataTransfer): KaimoDropSnapshot;
        handleDrop(snapshot: KaimoDropSnapshot, inputId: string, dotNetRef: DotNetObjectReference, maxFiles?: number): Promise<void>;
        openFilePicker(inputId?: string): void;
        pickFolder(dotNetRef: DotNetObjectReference, inputId?: string, maxFiles?: number): void;
        initPublicDrop(dropSelector: string, inputId: string, dotNetRef: DotNetObjectReference, maxFiles: number): void;
    };
    kaimoFileUploadAbortController?: AbortController;
    fileMarquee: {
        initialize(selector: string, dotNetRef: DotNetObjectReference): void;
        dispose(selector: string): void;
        scrollToIndex(selector: string, index: number, itemHeight: number, hasParent: boolean): void;
    };
    schedulePaint: {
        // root is never null from C# (ElementReference); the runtime guard stays anyway.
        initialize(root: KaimoSchedulePaintRoot, dotNetRef: DotNetObjectReference): void;
        dispose(root: KaimoSchedulePaintRoot | null): void;
    };
}

// columnResize.ts refers to itself by its bare global name, so it is a global var
// (which also makes window.columnResize typed).
declare var columnResize: {
    _onMouseMove: ((e: MouseEvent) => void) | null;
    _onMouseUp: (() => void) | null;
    start(dotNetRef: DotNetObjectReference, leftIndex: number, startX: number, minW: number): void;
    _cleanup(): void;
};

// The DotNetObjectReference C# passes to JS interop (Microsoft.JSInterop).
interface DotNetObjectReference {
    invokeMethodAsync<T = unknown>(methodName: string, ...args: unknown[]): Promise<T>;
}

// Vendored wwwroot/lib/mammoth (docx -> HTML), loaded before filePreview.js; only the part we use.
declare const mammoth: {
    convertToHtml(input: { arrayBuffer: ArrayBuffer }): Promise<{ value: string }>;
};

// schedulePaint.ts stores its cleanup callback on the grid element.
interface KaimoSchedulePaintRoot extends HTMLElement {
    _schedulePaintCleanup?: () => void;
}

// fileUpload.ts: a drop captured synchronously, and the files/folders collected from it.
interface KaimoDropSnapshot {
    entries: FileSystemEntry[];
    loose: File[];
}

interface KaimoCollectedFiles {
    files: File[];
    dirs: string[];
    emptyDirs: string[];
}

// fileMarquee.ts: rectangles in viewport (x) and scroll-content (y) coordinates.
interface KaimoRect {
    left: number;
    right: number;
    top: number;
    bottom: number;
}

interface KaimoMarqueeGesture {
    startX: number;
    startContentY: number;
    currentX: number;
    currentY: number;
    button: number;
    addToSelection: boolean;
    active: boolean;
    rowGeometry: Map<string, KaimoRect>;
}

// Result of FileBrowser.OnListNavigationKey.
interface KaimoListNavigationResult {
    index: number;
    path: string;
    itemHeight: number;
    hasParent: boolean;
}

interface KaimoScheduleSlot {
    element: HTMLButtonElement;
    row: number;
    hour: number;
}

// avatarCrop.ts keeps its per-instance state on the canvas element itself.
interface KaimoCropState {
    img: HTMLImageElement;
    base: number;
    zoom: number;
    size: number;
    ox: number;
    oy: number;
    dragging: boolean;
    lx: number;
    ly: number;
}

interface KaimoCropCanvas extends HTMLCanvasElement {
    _crop?: KaimoCropState;
}
