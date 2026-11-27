(() => {
    'use strict';

    const WIDTH = 1400;
    const HEIGHT = 850;
    const MAX_POINTS = 4500;
    const state = {
        connection: null, room: '', role: 'Viewer', tool: 'pen', color: '#222222', width: 5,
        canDraw: false, drawing: false, current: null, pointerId: null,
        strokes: [], pending: new Map(), nextPendingId: 0,
        joining: false, requestedRoom: '', eventBuffer: []
    };
    let canvas;
    let ctx;

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
        ctx.strokeStyle = stroke.isEraser ? '#ffffff' : stroke.color;
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
        // A radír fehér háttéren fehér vonallal takar.
        ctx.fillStyle = '#ffffff';
        ctx.fillRect(0, 0, WIDTH, HEIGHT);
        for (const stroke of state.strokes) drawStroke(stroke);
        for (const stroke of state.pending.values()) drawStroke(stroke);
        if (state.current) drawStroke(state.current);
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
            case 'cleared':
                state.strokes = [];
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
            applySnapshot(snapshot);
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
        connection.onreconnecting(() => {
            state.canDraw = false;
            state.current = null;
            state.drawing = false;
            state.pending.clear();
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
            redraw();
            showStatus('A szerverkapcsolat megszakadt.', false);
            updateParticipantLabel();
        });
        state.connection = connection;
        await connection.start();
        updateParticipantLabel();
        return connection;
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
            points: [coords(event)]
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
        if (points.length > MAX_POINTS) state.current.points = points.filter((_, i) => i % 2 === 0);
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
            redraw();
            showStatus('A vonal nem menthető: nincs kapcsolat vagy jogosultság.');
            return;
        }
        const key = ++state.nextPendingId;
        const room = state.room;
        state.pending.set(key, stroke);
        redraw();
        try {
            const saved = await state.connection.invoke('SendStroke', room, stroke);
            state.pending.delete(key);
            if (state.room === room) mergeStroke(saved);
            redraw();
        } catch (error) {
            state.pending.delete(key);
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
        exportPng() {
            if (!canvas) return;
            const link = document.createElement('a');
            link.download = `rajz-${state.room || 'vaszon'}.png`;
            link.href = canvas.toDataURL('image/png');
            link.click();
        }
    };

    function initialize() {
        canvas = $('drawingCanvas');
        if (!canvas) return;
        ctx = canvas.getContext('2d');
        redraw();
        showRole('Viewer');
        canvas.addEventListener('pointerdown', start);
        canvas.addEventListener('pointermove', move);
        canvas.addEventListener('pointerup', finish);
        canvas.addEventListener('pointercancel', () => {
            state.drawing = false;
            state.current = null;
            state.pointerId = null;
            redraw();
        });
        $('colorPicker').addEventListener('input', event => state.color = event.target.value);
        $('lineWidth').addEventListener('input', event => state.width = Number(event.target.value));
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize);
    else initialize();
})();
