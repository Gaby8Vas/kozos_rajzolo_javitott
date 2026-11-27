(() => {
    'use strict';

    const WIDTH = 1400;
    const HEIGHT = 850;
    const MAX_POINTS = 4500;
    // Ennyi ezredmásodpercenként küldjük el rajzolás közben az új pontokat.
    const LIVE_INTERVAL_MS = 40;
    // Ennyi idő után eltűnik az az élő előnézet, amelyhez nem jött frissítés.
    const LIVE_TIMEOUT_MS = 15000;
    const state = {
        connection: null, room: '', role: 'Viewer', tool: 'pen', color: '#222222', width: 5,
        canDraw: false, drawing: false, current: null, pointerId: null,
        strokes: [], pending: new Map(), nextPendingId: 0,
        live: new Map(), clientSeq: 0,
        joining: false, requestedRoom: '', eventBuffer: []
    };
    let canvas;
    let ctx;
    let keyboardBound = false;

    const $ = id => document.getElementById(id);
    const connected = () => typeof signalR !== 'undefined' &&
        state.connection?.state === signalR.HubConnectionState.Connected;
    const showStatus = (message, isConnected = null) => {
        if ($('statusMessage')) $('statusMessage').textContent = message;
        if (isConnected !== null && $('connectionLabel')) {
            $('connectionLabel').textContent = isConnected ? 'Csatlakozva' : 'Nincs csatlakozva';
        }
    };
    const showRole = role => {
        state.role = role;
        state.canDraw = role === 'Owner' || role === 'Editor';
        $('roleLabel').textContent = 'Jogosultság: ' + role;
        $('clearButton').disabled = role !== 'Owner';
        $('undoButton').disabled = !state.canDraw;
        if ($('redoButton')) $('redoButton').disabled = !state.canDraw;
        $('ownerControls').hidden = role !== 'Owner';
    };
    const updateParticipantLabel = () => {
        $('participantLabel').textContent = connected() ? 'Szerverkapcsolat él' : 'Csatlakozásra vár';
    };

    function drawStroke(stroke) {
        if (!stroke?.points?.length) return;
        ctx.save();
        ctx.lineCap = 'round';
        ctx.lineJoin = 'round';
        // A radír valóban törli a képpontokat (átlátszóvá teszi), nem fehérrel takar.
        ctx.globalCompositeOperation = stroke.isEraser ? 'destination-out' : 'source-over';
        ctx.strokeStyle = stroke.isEraser ? '#000000' : stroke.color;
        ctx.lineWidth = stroke.width;
        if (stroke.points.length === 1) {
            const p = stroke.points[0];
            ctx.fillStyle = ctx.strokeStyle;
            ctx.beginPath();
            ctx.arc(p.x, p.y, stroke.width / 2, 0, Math.PI * 2);
            ctx.fill();
        } else {
            ctx.beginPath();
            ctx.moveTo(stroke.points[0].x, stroke.points[0].y);
            for (let i = 1; i < stroke.points.length; i++)
                ctx.lineTo(stroke.points[i].x, stroke.points[i].y);
            ctx.stroke();
        }
        ctx.restore();
    }

    function redraw() {
        if (!ctx) return;
        // Átlátszó vászon: a háttér (rács) a CSS-ből jön, a radír így ténylegesen töröl.
        ctx.clearRect(0, 0, WIDTH, HEIGHT);
        for (const stroke of state.strokes) drawStroke(stroke);
        for (const stroke of state.pending.values()) drawStroke(stroke);
        const now = Date.now();
        for (const [key, stroke] of state.live) {
            if (now - stroke.updated > LIVE_TIMEOUT_MS) state.live.delete(key);
            else drawStroke(stroke);
        }
        if (state.current) drawStroke(state.current);
    }

    // Rajzolás közben a még el nem küldött pontokat továbbítjuk a többieknek.
    function streamCurrent(force = false) {
        const stroke = state.current;
        if (!stroke || !connected() || !state.canDraw || !state.room) return;
        const now = Date.now();
        if (!force && now - stroke.lastSent < LIVE_INTERVAL_MS) return;
        const reset = stroke.needsReset === true;
        const from = reset ? 0 : stroke.sentCount;
        if (from >= stroke.points.length) return;
        let points = stroke.points.slice(from);
        if (points.length > 1000) {
            points = points.slice(-1000);
        }
        stroke.lastSent = now;
        stroke.sentCount = stroke.points.length;
        stroke.needsReset = false;
        state.connection.send('StreamStroke', state.room, {
            clientKey: stroke.clientKey, color: stroke.color, width: stroke.width,
            isEraser: stroke.isEraser, reset, points
        }).catch(() => { /* Az előnézet elvesztése nem hiba. */ });
    }

    function cancelLive(stroke) {
        if (!stroke?.sentCount || !connected() || !state.room) return;
        state.connection.send('CancelLiveStroke', state.room, stroke.clientKey).catch(() => {});
    }

    function coords(event) {
        const r = canvas.getBoundingClientRect();
        return {
            x: Math.max(0, Math.min(WIDTH, (event.clientX - r.left) * WIDTH / r.width)),
            y: Math.max(0, Math.min(HEIGHT, (event.clientY - r.top) * HEIGHT / r.height))
        };
    }

    function mergeStroke(stroke) {
        if (!stroke || !Number.isSafeInteger(stroke.id) || stroke.id < 1) return;
        if (stroke.liveKey) state.live.delete(stroke.liveKey);
        if (state.strokes.some(item => item.id === stroke.id)) return;
        state.strokes.push(stroke);
        state.strokes.sort((a, b) => a.id - b.id);
        redraw();
    }

    function processEvent(type, room, args) {
        if (room !== state.room) return;
        switch (type) {
            case 'stroke':
                mergeStroke(args[0]);
                break;
            case 'removed':
                state.strokes = state.strokes.filter(s => s.id !== args[0]);
                redraw();
                break;
            case 'live': {
                const live = args[0];
                if (!live?.clientKey || !Array.isArray(live.points)) break;
                let entry = state.live.get(live.clientKey);
                if (!entry) {
                    entry = { points: [] };
                    state.live.set(live.clientKey, entry);
                }
                entry.color = live.color;
                entry.width = live.width;
                entry.isEraser = live.isEraser;
                entry.points = live.reset ? live.points.slice() : entry.points.concat(live.points);
                entry.updated = Date.now();
                redraw();
                break;
            }
            case 'liveCancelled':
                if (state.live.delete(args[0])) redraw();
                break;
            case 'liveGone': {
                let changed = false;
                for (const key of [...state.live.keys()])
                    if (key.startsWith(args[0])) { state.live.delete(key); changed = true; }
                if (changed) redraw();
                break;
            }
            case 'cleared':
                state.strokes = [];
                state.live.clear();
                state.pending.clear();
                state.current = null;
                state.drawing = false;
                redraw();
                break;
            case 'role':
                if (args[0] === $('currentUserId').value) {
                    showRole(args[1]);
                    if (!state.canDraw) {
                        state.current = null;
                        state.drawing = false;
                        state.pending.clear();
                        redraw();
                    }
                    showStatus('A jogosultságod megváltozott: ' + args[1], true);
                }
                if (state.role === 'Owner') void loadParticipants();
                break;
            case 'joined':
                if (state.role === 'Owner') void loadParticipants();
                showStatus(args[0] + ' csatlakozott a szobához.', true);
                break;
            case 'left':
                if (state.role === 'Owner') void loadParticipants();
                showStatus(args[0] + ' elhagyta a szobát.', true);
                break;
        }
    }

    function receive(type, room, ...args) {
        if (state.joining) {
            // A csatlakozás alatt érkező események átmenetileg a pufferbe kerülnek.
            state.eventBuffer.push({ type, room, args });
            return;
        }
        processEvent(type, room, args);
    }

    function applySnapshot(snapshot) {
        state.room = snapshot.roomCode;
        state.requestedRoom = '';
        state.strokes = Array.isArray(snapshot.strokes)
            ? snapshot.strokes.filter(s => Number.isSafeInteger(s.id)).sort((a, b) => a.id - b.id)
            : [];
        state.pending.clear();
        state.live.clear();
        state.current = null;
        state.drawing = false;
        showRole(snapshot.role || 'Viewer');
        $('roomCode').value = snapshot.roomCode;
        $('roomLabel').textContent = snapshot.roomName + ' (' + snapshot.roomCode + ')';
        const buffered = state.eventBuffer;
        state.eventBuffer = [];
        state.joining = false;
        redraw();
        for (const event of buffered) processEvent(event.type, event.room, event.args);
        showStatus('Csatlakozva: ' + snapshot.roomName + ' (' + snapshot.roomCode + ')', true);
        updateParticipantLabel();
        if (state.role === 'Owner') void loadParticipants();
        void loadMyRooms();
    }

    async function beginJoin(method, argument, requestedRoom) {
        if (!connected()) throw new Error('Nincs szerverkapcsolat.');
        if (state.joining) throw new Error('A csatlakozás már folyamatban van.');
        state.joining = true;
        state.requestedRoom = requestedRoom;
        state.eventBuffer = [];
        state.current = null;
        state.drawing = false;
        try {
            const snapshot = await state.connection.invoke(method, argument);
            // A nagy rajzok előzményei adagokban jönnek; közben az események pufferben várnak.
            const strokes = Array.isArray(snapshot.strokes) ? snapshot.strokes.slice() : [];
            let hasMore = snapshot.hasMore === true;
            while (hasMore) {
                showStatus('Rajz betöltése… (' + strokes.length + ' vonal)');
                const lastId = strokes.length ? strokes[strokes.length - 1].id : 0;
                const page = await state.connection.invoke('GetStrokePage', snapshot.roomCode, lastId);
                strokes.push(...(page.strokes || []));
                hasMore = page.hasMore === true && (page.strokes || []).length > 0;
            }
            applySnapshot({ ...snapshot, strokes });
        } catch (error) {
            state.joining = false;
            state.requestedRoom = '';
            state.eventBuffer = [];
            throw error;
        }
    }

    async function ensureConnection() {
        if (connected()) return state.connection;
        if (state.connection && state.connection.state !== signalR.HubConnectionState.Disconnected)
            throw new Error('A kapcsolat éppen helyreáll.');
        if (state.connection) {
            try { await state.connection.stop(); } catch { /* Már megszakadt kapcsolat. */ }
        }
        if (typeof signalR === 'undefined') {
            throw new Error('A SignalR klienskönyvtár nem töltődött be. Ellenőrizd az internetet vagy a helyi JS-fájlt.');
        }
        const connection = new signalR.HubConnectionBuilder()
            .withUrl('/drawingHub')
            .withAutomaticReconnect([0, 1500, 3000, 8000, 15000])
            .build();

        connection.on('ReceiveStroke', (room, stroke) => receive('stroke', room, stroke));
        connection.on('StrokeRemoved', (room, id) => receive('removed', room, id));
        connection.on('RoomCleared', room => receive('cleared', room));
        connection.on('ParticipantRoleChanged', (room, userId, role) => receive('role', room, userId, role));
        connection.on('UserJoined', (room, name) => receive('joined', room, name));
        connection.on('UserLeft', (room, name) => receive('left', room, name));
        connection.on('LiveStroke', (room, live) => receive('live', room, live));
        connection.on('LiveStrokeCancelled', (room, key) => receive('liveCancelled', room, key));
        connection.on('LiveConnectionGone', (room, prefix) => receive('liveGone', room, prefix));
        connection.onreconnecting(() => {
            state.canDraw = false;
            state.current = null;
            state.drawing = false;
            state.pending.clear();
            state.live.clear();
            redraw();
            showStatus('Kapcsolatvesztés. Újracsatlakozás…', false);
            updateParticipantLabel();
        });
        connection.onreconnected(async () => {
            try {
                if (state.room) await beginJoin('JoinRoom', state.room, state.room);
                else showStatus('A szerverkapcsolat helyreállt.', true);
            } catch (error) {
                showStatus('Az újracsatlakozás után nem sikerült a szoba betöltése: ' + error.message, false);
            }
        });
        connection.onclose(() => {
            state.canDraw = false;
            state.current = null;
            state.drawing = false;
            state.pending.clear();
            state.live.clear();
            redraw();
            showStatus('A szerverkapcsolat megszakadt.', false);
            updateParticipantLabel();
        });
        state.connection = connection;
        await connection.start();
        updateParticipantLabel();
        return connection;
    }

    const ROLE_LABELS = { Owner: 'tulajdonos', Editor: 'szerkesztő', Viewer: 'megtekintő' };

    async function loadMyRooms() {
        const list = $('myRooms');
        if (!list || !connected()) return;
        try {
            const rooms = await state.connection.invoke('GetMyRooms');
            list.replaceChildren();
            if (!rooms.length) {
                const empty = document.createElement('small');
                empty.textContent = 'Még nincs szobád. Hozz létre egyet, vagy csatlakozz kóddal.';
                list.append(empty);
                return;
            }
            for (const room of rooms) {
                const button = document.createElement('button');
                button.type = 'button';
                button.className = 'room-chip' + (room.roomCode === state.room ? ' current' : '');
                const title = document.createElement('span');
                title.textContent = room.name;
                const meta = document.createElement('small');
                meta.textContent = room.roomCode + ' · ' + (ROLE_LABELS[room.role] || room.role);
                button.append(title, meta);
                button.addEventListener('click', () => {
                    $('roomCode').value = room.roomCode;
                    void window.sharedDrawing.join();
                });
                list.append(button);
            }
        } catch (error) {
            list.textContent = 'A szobák listája nem tölthető be: ' + error.message;
        }
    }

    async function loadParticipants() {
        if (!connected() || state.role !== 'Owner' || !state.room) return;
        const activeRoom = state.room;
        try {
            const members = await state.connection.invoke('GetParticipants', activeRoom);
            if (state.room !== activeRoom) return;
            const select = $('participantSelect');
            const prior = select.value;
            select.replaceChildren(new Option('Tag kiválasztása', ''));
            for (const member of members) {
                const option = new Option(member.displayName + ' (' + member.role + ')', member.userId);
                select.add(option);
            }
            if (members.some(m => m.userId === prior)) select.value = prior;
            $('membersStatus').textContent = members.length + ' tag szerepel a szobában.';
        } catch (error) {
            $('membersStatus').textContent = error.message;
        }
    }

    function start(event) {
        if (!state.room || !connected() || state.joining) {
            showStatus('Rajzoláshoz élő szobakapcsolat szükséges.');
            return;
        }
        if (!state.canDraw) {
            showStatus('Csak megtekintői jogosultságod van.');
            return;
        }
        if (state.drawing) return;
        event.preventDefault();
        canvas.setPointerCapture?.(event.pointerId);
        state.pointerId = event.pointerId;
        state.drawing = true;
        state.current = {
            color: state.color,
            width: state.width,
            isEraser: state.tool === 'eraser',
            points: [coords(event)],
            clientKey: 'c' + (++state.clientSeq),
            sentCount: 0, lastSent: 0, needsReset: false
        };
        redraw();
    }

    function move(event) {
        if (!state.drawing || !state.current || state.pointerId !== event.pointerId) return;
        const p = coords(event);
        const points = state.current.points;
        const last = points[points.length - 1];
        if ((p.x - last.x) ** 2 + (p.y - last.y) ** 2 < 1.5 * 1.5) return;
        points.push(p);
        // Túl sok pont esetén ritkítjuk a pontsort.
        if (points.length > MAX_POINTS) {
            state.current.points = points.filter((_, i) => i % 2 === 0);
            state.current.needsReset = true;
        }
        streamCurrent();
        redraw();
    }

    async function finish(event) {
        if (!state.drawing || !state.current) return;
        if (event?.pointerId !== undefined && state.pointerId !== event.pointerId) return;
        state.drawing = false;
        state.pointerId = null;
        const stroke = state.current;
        state.current = null;
        if (!stroke.points.length) { redraw(); return; }
        if (!connected() || !state.canDraw || !state.room) {
            cancelLive(stroke);
            redraw();
            showStatus('A vonal nem menthető: nincs kapcsolat vagy jogosultság.');
            return;
        }
        const key = ++state.nextPendingId;
        const room = state.room;
        state.pending.set(key, stroke);
        redraw();
        try {
            const saved = await state.connection.invoke('SendStroke', room, {
                color: stroke.color, width: stroke.width, isEraser: stroke.isEraser,
                points: stroke.points, clientKey: stroke.clientKey
            });
            state.pending.delete(key);
            if (state.room === room) mergeStroke(saved);
            redraw();
        } catch (error) {
            state.pending.delete(key);
            cancelLive(stroke);
            redraw();
            showStatus('A szerver nem fogadta el a vonalat: ' + error.message);
        }
    }

    window.sharedDrawing = {
        async join() {
            const code = $('roomCode').value.trim().replace(/[^a-z0-9]/gi, '').toUpperCase();
            if (code.length < 3) { showStatus('Adj meg egy érvényes szobakódot.'); return; }
            try {
                await ensureConnection();
                await beginJoin('JoinRoom', code, code);
            } catch (error) { showStatus('Nem sikerült csatlakozni: ' + error.message); }
        },
        async newRoom() {
            try {
                await ensureConnection();
                await beginJoin('CreateRoom', $('roomName').value, '');
                showStatus('Új szoba létrejött. Kód: ' + state.room, true);
            } catch (error) { showStatus('Nem sikerült létrehozni a szobát: ' + error.message); }
        },
        setTool(tool) {
            state.tool = tool;
            $('penTool').classList.toggle('active', tool === 'pen');
            $('eraserTool').classList.toggle('active', tool === 'eraser');
        },
        setColor(color) {
            state.color = color;
            $('colorPicker').value = color;
            this.setTool('pen');
        },
        async undo() {
            if (!connected() || !state.canDraw || !state.room) return;
            if (state.pending.size > 0) { showStatus('Előbb fejeződjön be a rajz mentése.'); return; }
            try {
                const removed = await state.connection.invoke('UndoLastStroke', state.room);
                showStatus(removed ? 'Az utolsó saját vonal visszavonva.' : 'Nincs saját visszavonható vonal.', true);
            } catch (error) { showStatus('A visszavonás nem sikerült: ' + error.message); }
        },
        async redo() {
            if (!connected() || !state.canDraw || !state.room) return;
            if (state.pending.size > 0) { showStatus('Előbb fejeződjön be a rajz mentése.'); return; }
            try {
                const restored = await state.connection.invoke('RedoLastStroke', state.room);
                showStatus(restored ? 'A visszavont vonal visszaállítva.' : 'Nincs visszaállítható vonal.', true);
            } catch (error) { showStatus('Az újra végrehajtás nem sikerült: ' + error.message); }
        },
        async clear() {
            if (!connected() || state.role !== 'Owner' || !state.room) return;
            if (!window.confirm('Biztosan törlöd a teljes közös rajzot?')) return;
            try { await state.connection.invoke('ClearRoom', state.room); }
            catch (error) { showStatus('A törlés nem sikerült: ' + error.message); }
        },
        async saveRole() {
            if (!connected() || state.role !== 'Owner') return;
            const target = $('participantSelect').value;
            const role = $('participantRole').value;
            if (!target) { $('membersStatus').textContent = 'Válassz ki egy tagot.'; return; }
            try {
                await state.connection.invoke('SetParticipantRole', state.room, target, role);
                $('membersStatus').textContent = 'A jogosultság mentve.';
                await loadParticipants();
            } catch (error) { $('membersStatus').textContent = error.message; }
        },
        init() { initialize(); },
        // Csak teszteléshez: az élő előnézetek száma.
        _liveCount() { return state.live.size; },
        _strokeCount() { return state.strokes.length; },
        refreshRooms() { return loadMyRooms(); },
        exportPng() {
            if (!canvas) return;
            // A letöltött képnek fehér háttere legyen, ne átlátszó.
            const exportCanvas = document.createElement('canvas');
            exportCanvas.width = WIDTH;
            exportCanvas.height = HEIGHT;
            const exportCtx = exportCanvas.getContext('2d');
            exportCtx.fillStyle = '#ffffff';
            exportCtx.fillRect(0, 0, WIDTH, HEIGHT);
            exportCtx.drawImage(canvas, 0, 0);
            const link = document.createElement('a');
            link.download = `rajz-${state.room || 'vaszon'}.png`;
            link.href = exportCanvas.toDataURL('image/png');
            link.click();
        }
    };

    function initialize() {
        const element = $('drawingCanvas');
        // Ugyanarra a vászonra nem kötünk rá kétszer; új (újrarenderelt) vászonra igen.
        if (!element || element === canvas) return;
        canvas = element;
        ctx = canvas.getContext('2d');
        redraw();
        showRole('Viewer');
        canvas.addEventListener('pointerdown', start);
        // A saját szobák listájához rögtön kapcsolódunk a szerverhez.
        ensureConnection()
            .then(() => loadMyRooms())
            .catch(error => showStatus('Nincs szerverkapcsolat: ' + error.message, false));
        canvas.addEventListener('pointermove', move);
        canvas.addEventListener('pointerup', finish);
        canvas.addEventListener('pointercancel', () => {
            cancelLive(state.current);
            state.drawing = false;
            state.current = null;
            state.pointerId = null;
            redraw();
        });
        $('colorPicker').addEventListener('input', event => state.color = event.target.value);
        if (!keyboardBound) {
            keyboardBound = true;
            // Ctrl+Z: visszavonás, Ctrl+Y vagy Ctrl+Shift+Z: újra (szövegmezőben nem).
            document.addEventListener('keydown', event => {
                if (!(event.ctrlKey || event.metaKey)) return;
                const tag = event.target?.tagName;
                if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
                const key = (event.key || '').toLowerCase();
                if (key === 'z' && !event.shiftKey) { event.preventDefault(); void window.sharedDrawing.undo(); }
                else if (key === 'y' || (key === 'z' && event.shiftKey)) { event.preventDefault(); void window.sharedDrawing.redo(); }
            });
        }
        $('lineWidth').addEventListener('input', event => state.width = Number(event.target.value));
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize);
    else initialize();
})();
