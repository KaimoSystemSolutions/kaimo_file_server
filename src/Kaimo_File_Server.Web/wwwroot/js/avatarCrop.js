// Square avatar cropper: pan (drag), zoom (wheel + buttons), then export a square JPEG.
// State lives on the canvas element so multiple instances never collide. All interaction is
// client-side so panning stays smooth on the Blazor Server circuit.
window.kaimoAvatarCrop = (function () {
    function clamp(canvas) {
        const st = canvas._crop;
        const s = st.base * st.zoom;
        const w = st.img.naturalWidth * s;
        const h = st.img.naturalHeight * s;
        // Keep the image covering the frame: left/top <= 0 and right/bottom >= size.
        st.ox = Math.min(0, Math.max(st.size - w, st.ox));
        st.oy = Math.min(0, Math.max(st.size - h, st.oy));
    }

    function draw(canvas) {
        const st = canvas._crop;
        const ctx = canvas.getContext('2d');
        const s = st.base * st.zoom;
        clamp(canvas);
        ctx.clearRect(0, 0, st.size, st.size);
        ctx.imageSmoothingQuality = 'high';
        ctx.drawImage(st.img, st.ox, st.oy, st.img.naturalWidth * s, st.img.naturalHeight * s);
    }

    return {
        init: function (canvas, dataUrl, size) {
            return new Promise((resolve, reject) => {
                const img = new Image();
                img.onload = () => {
                    canvas.width = size;
                    canvas.height = size;
                    const base = Math.max(size / img.naturalWidth, size / img.naturalHeight);
                    const st = {
                        img, base, zoom: 1, size,
                        ox: (size - img.naturalWidth * base) / 2,
                        oy: (size - img.naturalHeight * base) / 2,
                        dragging: false, lx: 0, ly: 0
                    };
                    canvas._crop = st;

                    canvas.onpointerdown = (e) => {
                        st.dragging = true; st.lx = e.clientX; st.ly = e.clientY;
                        try { canvas.setPointerCapture(e.pointerId); } catch { }
                    };
                    canvas.onpointermove = (e) => {
                        if (!st.dragging) return;
                        st.ox += e.clientX - st.lx; st.oy += e.clientY - st.ly;
                        st.lx = e.clientX; st.ly = e.clientY;
                        draw(canvas);
                    };
                    const stop = (e) => {
                        st.dragging = false;
                        try { canvas.releasePointerCapture(e.pointerId); } catch { }
                    };
                    canvas.onpointerup = stop;
                    canvas.onpointercancel = stop;
                    canvas.onwheel = (e) => {
                        e.preventDefault();
                        st.zoom = Math.min(5, Math.max(1, st.zoom * Math.exp(-e.deltaY * 0.0015)));
                        draw(canvas);
                    };

                    draw(canvas);
                    resolve(true);
                };
                img.onerror = () => reject('load-failed');
                img.src = dataUrl;
            });
        },

        // factor > 1 zooms in, < 1 zooms out (for the +/- buttons).
        zoom: function (canvas, factor) {
            const st = canvas && canvas._crop;
            if (!st) return;
            st.zoom = Math.min(5, Math.max(1, st.zoom * factor));
            draw(canvas);
        },

        result: function (canvas) {
            return canvas.toDataURL('image/jpeg', 0.9);
        }
    };
})();
