// Böngészőoldali állapotkezelés ellenőrzése Node.js alatt.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const js = fs.readFileSync(path.join(__dirname, '../kozos_rajzolo/wwwroot/js/drawing.js'), 'utf8');
const hub = { strokes: [], next: 1, users: [], role: new Map(), connections: [], liveSent: 0 };

function page(userId, userName) {
    const elements = new Map();
    const ids = ['statusMessage', 'connectionLabel', 'roleLabel', 'clearButton', 'undoButton',
        'ownerControls', 'participantLabel', 'roomCode', 'roomName', 'roomLabel',
        'currentUserId', 'userName', 'participantSelect', 'participantRole', 'membersStatus',
        'penTool', 'eraserTool', 'colorPicker', 'lineWidth'];
    for (const id of ids) elements.set(id, {
        id, textContent: '', value: '', hidden: false, disabled: false, innerHTML: '',
        classList: { toggle() {} }, addEventListener() {}, children: [],
        replaceChildren(...x) { this.children = x; }, add(opt) { this.children.push(opt); }
    });
    elements.get('roomName').value = 'Teszt szoba';
    elements.get('userName').value = userName;
    elements.get('currentUserId').value = userId;
    elements.get('participantRole').value = 'Editor';
    const canvas = { id: 'drawingCanvas', events: {}, width: 1400, height: 850,
        getContext() { return { save() {}, restore() {}, fillRect() {}, clearRect() {}, beginPath() {},
            arc() {}, fill() {}, moveTo() {}, lineTo() {}, stroke() {} }; },
        addEventListener(name, fn) { this.events[name] = fn; },
        getBoundingClientRect() { return { left: 0, top: 0, width: 1400, height: 850 }; },
        setPointerCapture() {}, toDataURL() { return 'data:image/png;base64,AAA'; }
    };
    elements.set('drawingCanvas', canvas);
    const pageInfo = { userId, userName, elements, canvas };
    class FakeConnection {
        constructor() { this.state = 'Disconnected'; this.handlers = new Map(); this.id = 'conn' + hub.connections.length; hub.connections.push(this); }
        on(name, fn) { this.handlers.set(name, fn); }
        onreconnected(fn) { this.reconnected = fn; }
        onreconnecting(fn) { this.reconnecting = fn; }
        onclose(fn) { this.closed = fn; }
        async start() { this.state = 'Connected'; }
        async stop() { this.state = 'Disconnected'; }
        fire(name, ...args) { this.handlers.get(name)?.(...args); }
        async send(method, ...args) {
            const key = c => this.id + ':' + c;
            if (method === 'StreamStroke') {
                if (hub.role.get(userId) === 'Viewer') return;
                hub.liveSent++;
                for (const c of hub.connections) if (c !== this) c.fire('LiveStroke', args[0], {...args[1], clientKey: key(args[1].clientKey)});
            } else if (method === 'CancelLiveStroke') {
                for (const c of hub.connections) if (c !== this) c.fire('LiveStrokeCancelled', args[0], key(args[1]));
            } else throw new Error('Unimplemented send: ' + method);
        }
        async invoke(method, ...args) {
            switch(method) {
                case 'CreateRoom': hub.users.push(pageInfo); hub.role.set(userId, 'Owner');
                    return { roomCode: 'ABCD2345', roomName: args[0], role: 'Owner', strokes: [...hub.strokes] };
                case 'JoinRoom': hub.users.push(pageInfo);
                    if (!hub.role.has(userId)) hub.role.set(userId, 'Viewer');
                    return { roomCode: args[0], roomName: 'Teszt szoba', role: hub.role.get(userId), strokes: [...hub.strokes] };
                case 'SendStroke': {
                    if (pageInfo.failNext) { pageInfo.failNext = false; throw new Error('próba mentési hiba'); }
                    if (hub.role.get(userId) === 'Viewer') throw new Error('megtekintő');
                    const item = {...args[1], id: hub.next++, userName, liveKey: this.id + ':' + args[1].clientKey};
                    hub.strokes.push(item);
                    for (const c of hub.connections) if (c !== this) c.fire('ReceiveStroke', args[0], item);
                    return item;
                }
                case 'GetParticipants': return hub.users.map(u => ({userId:u.userId,displayName:u.userName,role:hub.role.get(u.userId)}));
                case 'SetParticipantRole': {
                    hub.role.set(args[1], args[2]);
                    for (const c of hub.connections) c.fire('ParticipantRoleChanged', args[0], args[1], args[2]);
                    return;
                }
                case 'UndoLastStroke': {
                    const index = hub.strokes.findLastIndex(s => s.userName === userName);
                    if (index < 0) return false;
                    const [item] = hub.strokes.splice(index, 1);
                    for (const c of hub.connections) c.fire('StrokeRemoved', args[0], item.id);
                    return true;
                }
                case 'ClearRoom': {
                    hub.strokes = [];
                    for (const c of hub.connections) c.fire('RoomCleared', args[0]);
                    return;
                }
                default: throw new Error('Unimplemented: ' + method);
            }
        }
    }
    const signalR = { HubConnectionState: { Connected: 'Connected', Disconnected: 'Disconnected' },
        HubConnectionBuilder: class {
            withUrl() { return this; } withAutomaticReconnect() { return this; }
            build() { const c = new FakeConnection(); pageInfo.connection = c; return c; }
        } };
    const document = { readyState: 'complete', getElementById: id => elements.get(id),
        addEventListener() {}, createElement: tag => tag === 'canvas'
            ? { getContext: () => ({ fillRect() {}, drawImage() { pageInfo.exported = true; } }),
                toDataURL: () => 'data:image/png;base64,BBB' }
            : { click() { pageInfo.downloaded = this.href; } } };
    const context = {
        document, signalR, console,
        Option: class { constructor(text, value) { this.textContent = text; this.value = value; } },
        window: { confirm: () => true },
        Math, Number, Map, Promise, Array, Error
    };
    vm.createContext(context);
    vm.runInContext(js, context);
    pageInfo.api = context.window.sharedDrawing;
    return pageInfo;
}

async function draw(p, x) {
    p.canvas.events.pointerdown({pointerId:1, clientX:x, clientY:30, preventDefault() {}});
    p.canvas.events.pointermove({pointerId:1, clientX:x+30, clientY:50});
    await p.canvas.events.pointerup({pointerId:1});
}

(async () => {
    const owner = page('u1','same@example.org');
    await owner.api.newRoom();
    assert.equal(hub.role.get('u1'),'Owner');
    const duplicateSession = page('u1','same@example.org');
    duplicateSession.elements.get('roomCode').value = 'ABCD2345';
    await duplicateSession.api.join();
    const viewer = page('u2','other@example.org');
    viewer.elements.get('roomCode').value='ABCD2345';
    await viewer.api.join();
    assert.equal(hub.role.get('u2'),'Viewer');
    // Élő előnézet: rajzolás közben a többiek már látják a vonalat.
    owner.canvas.events.pointerdown({pointerId:1, clientX:40, clientY:30, preventDefault() {}});
    owner.canvas.events.pointermove({pointerId:1, clientX:70, clientY:50});
    await Promise.resolve();
    assert.ok(hub.liveSent >= 1, 'Rajzolás közben élő részlet megy ki.');
    assert.equal(viewer.api._liveCount(), 1, 'A megtekintő látja az élő vonalat.');
    await owner.canvas.events.pointerup({pointerId:1});
    assert.equal(viewer.api._liveCount(), 0, 'A mentett vonal felváltja az élő előnézetet.');
    assert.equal(hub.strokes.length,1);
    assert.equal(hub.strokes[0].id,1);
    // Azonos fiók második kapcsolata is fogadja a rajzot.
    duplicateSession.api.exportPng();
    assert.ok(duplicateSession.exported && duplicateSession.downloaded, 'A PNG-export fehér hátterű képet készít.');
    viewer.elements.get('participantRole').value='Editor';
    owner.elements.get('participantSelect').value='u2';
    await owner.api.saveRole();
    assert.equal(hub.role.get('u2'),'Editor');
    await draw(viewer,80);
    assert.equal(hub.strokes.length,2);
    viewer.failNext=true;
    await draw(viewer,120);
    assert.equal(hub.strokes.length,2,'Elutasított vonal nem kerülhet adatbázisba.');
    await viewer.api.undo();
    assert.equal(hub.strokes.length,1,'A visszavonás szerveroldalon is hatásos.');
    await owner.api.clear();
    assert.equal(hub.strokes.length,0);
    console.log('JS smoke tests: 12 ellenőrzés sikeres (böngésző és szerver szimulált).');
})().catch(e => { console.error(e); process.exitCode=1; });
