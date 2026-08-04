window.schedulePaint = {
    initialize: function (root, dotNetRef) {
        if (!root || !root.isConnected || root._schedulePaintCleanup) return;

        var dragging = false;
        var startSlot = null;
        var endSlot = null;

        function slotAt(clientX, clientY) {
            var element = document.elementFromPoint(clientX, clientY);
            var slot = element && element.closest('.cloud-schedule-slot');
            return slot && root.contains(slot) ? slot : null;
        }

        function readSlot(slot) {
            return {
                element: slot,
                row: Number(slot.dataset.scheduleRow),
                hour: Number(slot.dataset.scheduleHour)
            };
        }

        function clearPreview() {
            root.querySelectorAll('.cloud-schedule-slot--range-preview, .cloud-schedule-slot--range-origin, .cloud-schedule-slot--range-end')
                .forEach(function (slot) {
                    slot.classList.remove(
                        'cloud-schedule-slot--range-preview',
                        'cloud-schedule-slot--range-origin',
                        'cloud-schedule-slot--range-end');
                });
            root.classList.remove('cloud-schedule-scroll--range-active');
        }

        function preview(slot) {
            endSlot = readSlot(slot);
            var firstRow = Math.min(startSlot.row, endSlot.row);
            var lastRow = Math.max(startSlot.row, endSlot.row);
            var firstHour = Math.min(startSlot.hour, endSlot.hour);
            var lastHour = Math.max(startSlot.hour, endSlot.hour);

            clearPreview();
            root.classList.add('cloud-schedule-scroll--range-active');
            root.querySelectorAll('.cloud-schedule-slot').forEach(function (candidate) {
                var row = Number(candidate.dataset.scheduleRow);
                var hour = Number(candidate.dataset.scheduleHour);
                if (row >= firstRow && row <= lastRow && hour >= firstHour && hour <= lastHour)
                    candidate.classList.add('cloud-schedule-slot--range-preview');
            });
            startSlot.element.classList.add('cloud-schedule-slot--range-origin');
            endSlot.element.classList.add('cloud-schedule-slot--range-end');
        }

        function stop(commit) {
            if (!dragging) return;
            var selectionStart = startSlot;
            var selectionEnd = endSlot;
            dragging = false;
            document.removeEventListener('mousemove', move);
            document.removeEventListener('mouseup', finish);
            document.removeEventListener('keydown', keydown);
            window.removeEventListener('blur', cancel);
            clearPreview();

            if (commit && selectionStart && selectionEnd) {
                dotNetRef.invokeMethodAsync(
                    'PaintScheduleRectangleFromJs',
                    selectionStart.row,
                    selectionStart.hour,
                    selectionEnd.row,
                    selectionEnd.hour);
            }

            startSlot = null;
            endSlot = null;
        }

        function finish() {
            stop(true);
        }

        function cancel() {
            stop(false);
        }

        function keydown(event) {
            if (event.key === 'Escape') cancel();
        }

        function move(event) {
            if ((event.buttons & 1) === 0) {
                finish();
                return;
            }

            var slot = slotAt(event.clientX, event.clientY);
            if (slot && slot !== endSlot.element) preview(slot);
        }

        function start(event) {
            if (event.button !== 0) return;

            var slot = event.target.closest('.cloud-schedule-slot');
            if (!slot || !root.contains(slot) || slot.disabled) return;

            event.preventDefault();
            dragging = true;
            startSlot = readSlot(slot);
            preview(slot);
            document.addEventListener('mousemove', move);
            document.addEventListener('mouseup', finish);
            document.addEventListener('keydown', keydown);
            window.addEventListener('blur', cancel);
        }

        root.addEventListener('mousedown', start);
        root._schedulePaintCleanup = function () {
            cancel();
            root.removeEventListener('mousedown', start);
            delete root._schedulePaintCleanup;
        };
    },

    dispose: function (root) {
        if (root && root._schedulePaintCleanup) root._schedulePaintCleanup();
    }
};
