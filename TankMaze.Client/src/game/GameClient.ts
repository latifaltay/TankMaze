import { NetworkManager } from '../network/NetworkManager';
import { InputHandler } from './InputHandler';
import { WORLD_WIDTH, WORLD_HEIGHT, TANK_SIZE, GRID_SIZE } from './constants';
import { generateRandomNickname, readFixedNickname } from './nicknames';

const CELL_EMPTY = 0;
const CELL_WALL = 1;
const CELL_DESTRUCTIBLE = 2;
const CELL_BUSH = 3;
const NICK_LEN = 12;
const PLAYER_ENTRY_SIZE = 11 + NICK_LEN;
const PICKUP_SCORE = 1;
const PICKUP_HEALTH = 2;

type Player = {
    x: number; y: number; targetX: number; targetY: number;
    angle: number; health: number; maxHealth: number; nickname: string;
};

type Pickup = { x: number; y: number; type: number };
type LeaderboardEntry = { id: number; nickname: string; score: number };

export class GameClient {
    private canvas: HTMLCanvasElement;
    private ctx: CanvasRenderingContext2D;
    private players: Map<number, Player> = new Map();
    private bullets: { x: number; y: number }[] = [];
    private pickups: Pickup[] = [];
    private leaderboard: LeaderboardEntry[] = [];
    private myId = 0;
    private myScore = 0;
    private roomId = 0;
    private gamePhase: 'lobby' | 'playing' = 'lobby';
    private camera = { x: 0, y: 0 };
    private cols = WORLD_WIDTH / GRID_SIZE;
    private rows = WORLD_HEIGHT / GRID_SIZE;
    private gridMap: Uint8Array = new Uint8Array(this.cols * this.rows);
    private network!: NetworkManager;
    private input!: InputHandler;
    private lobbyOverlay: HTMLElement;
    private lobbyMessage: HTMLElement;
    private nickInput: HTMLInputElement;
    private playBtn: HTMLButtonElement;

    constructor() {
        this.canvas = document.getElementById('gameCanvas') as HTMLCanvasElement;
        this.ctx = this.canvas.getContext('2d')!;
        this.lobbyOverlay = document.getElementById('lobbyOverlay')!;
        this.lobbyMessage = document.getElementById('lobbyMessage')!;
        this.nickInput = document.getElementById('nickInput') as HTMLInputElement;
        this.playBtn = document.getElementById('playBtn') as HTMLButtonElement;

        this.resize();
        window.addEventListener('resize', () => this.resize());

        this.network = new NetworkManager((data) => this.processPacket(data));
        this.input = new InputHandler(
            this.network,
            () => this.myId,
            () => this.gamePhase === 'playing',
            this.players,
            this.isColliding
        );

        this.playBtn.addEventListener('click', () => this.onPlayClicked());
        this.nickInput.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') this.onPlayClicked();
        });

        this.render();
    }

    private resize() { this.canvas.width = window.innerWidth; this.canvas.height = window.innerHeight; }

    private onPlayClicked() {
        let nick = this.nickInput.value.trim();
        if (!nick) {
            nick = generateRandomNickname();
            this.nickInput.value = nick;
        }
        if (nick.length > NICK_LEN) nick = nick.slice(0, NICK_LEN);

        const bytes = new TextEncoder().encode(nick);
        const pkt = new Uint8Array(2 + bytes.length);
        pkt[0] = 0x06;
        pkt[1] = bytes.length;
        pkt.set(bytes, 2);
        this.network.send(pkt);
        this.network.send(new Uint8Array([0x07]));
        this.playBtn.disabled = true;
        this.lobbyMessage.textContent = 'Uygun harita aranıyor...';
    }

    private enterGame(roomId: number) {
        this.roomId = roomId;
        this.gamePhase = 'playing';
        this.playBtn.disabled = false;
        this.input.clearKeys();
        this.hideLobby();
    }

    private showLobby(message: string) {
        this.gamePhase = 'lobby';
        this.roomId = 0;
        this.leaderboard = [];
        this.myScore = 0;
        this.gridMap.fill(0);
        this.lobbyMessage.textContent = message;
        this.lobbyOverlay.classList.remove('hidden');
        this.playBtn.disabled = false;
        this.input.clearKeys();
        this.players.delete(this.myId);
        this.bullets = [];
        this.pickups = [];
    }

    private hideLobby() { this.lobbyOverlay.classList.add('hidden'); }

    private processPacket(buffer: ArrayBuffer) {
        const data = new DataView(buffer);
        const type = data.getUint8(0);

        if (type === 0x03) {
            this.myId = data.getUint16(1, true);
            this.nickInput.value = readFixedNickname(buffer, 3, NICK_LEN);
            return;
        }
        if (type === 0x04) {
            if (this.gamePhase === 'playing') this.gridMap.set(new Uint8Array(buffer, 1));
            return;
        }
        if (type === 0x0A) {
            const roomId = data.getUint16(1, true);
            this.enterGame(roomId);
            return;
        }
        if (type === 0x08) {
            if (this.gamePhase === 'playing') this.processLeaderboard(buffer);
            return;
        }
        if (type === 0x09) {
            this.showLobby('Öldün! Tekrar oynamak için Play\'e bas.');
            return;
        }
        if (type !== 0x02 || this.gamePhase !== 'playing') return;

        this.myId = data.getUint16(1, true);
        const playerCount = data.getUint16(3, true);
        let offset = 5;

        const visibleIds = new Set<number>();
        for (let i = 0; i < playerCount; i++) {
            const id = data.getUint16(offset, true);
            const x = data.getUint16(offset + 2, true);
            const y = data.getUint16(offset + 4, true);
            const angle = data.getUint8(offset + 6);
            const health = data.getInt16(offset + 7, true);
            const maxHealth = data.getInt16(offset + 9, true);
            const nickname = readFixedNickname(buffer, offset + 11, NICK_LEN);
            visibleIds.add(id);

            const p = this.players.get(id);
            if (p) {
                if (Math.abs(x - p.targetX) > 80 || Math.abs(y - p.targetY) > 80) { p.x = x; p.y = y; }
                p.targetX = x; p.targetY = y; p.angle = angle;
                p.health = health; p.maxHealth = maxHealth; p.nickname = nickname;
            } else {
                this.players.set(id, { x, y, targetX: x, targetY: y, angle, health, maxHealth, nickname });
            }
            offset += PLAYER_ENTRY_SIZE;
        }
        for (const id of this.players.keys()) {
            if (!visibleIds.has(id)) this.players.delete(id);
        }

        if (offset + 2 > data.byteLength) return;

        const bulletCount = data.getUint16(offset, true);
        offset += 2;
        this.bullets = [];
        for (let i = 0; i < bulletCount; i++) {
            this.bullets.push({
                x: data.getFloat32(offset, true),
                y: data.getFloat32(offset + 4, true)
            });
            offset += 8;
        }

        if (offset + 2 > data.byteLength) { this.pickups = []; return; }

        const pickupCount = data.getUint16(offset, true);
        offset += 2;
        this.pickups = [];
        for (let i = 0; i < pickupCount; i++) {
            this.pickups.push({
                x: data.getFloat32(offset, true),
                y: data.getFloat32(offset + 4, true),
                type: data.getUint8(offset + 8)
            });
            offset += 9;
        }
    }

    private processLeaderboard(buffer: ArrayBuffer) {
        const data = new DataView(buffer);
        const count = data.getUint16(1, true);
        const rowSize = 2 + 4 + NICK_LEN;
        this.leaderboard = [];
        let offset = 3;
        for (let i = 0; i < count; i++) {
            this.leaderboard.push({
                id: data.getUint16(offset, true),
                score: data.getInt32(offset + 2, true),
                nickname: readFixedNickname(buffer, offset + 6, NICK_LEN)
            });
            offset += rowSize;
        }
        this.leaderboard.sort((a, b) => b.score - a.score);
        const me = this.leaderboard.find(e => e.id === this.myId);
        if (me) this.myScore = me.score;
    }

    public isColliding = (newX: number, newY: number): boolean => {
        const margin = 5;
        const left = Math.floor((newX + margin) / GRID_SIZE);
        const right = Math.floor((newX + TANK_SIZE - margin) / GRID_SIZE);
        const top = Math.floor((newY + margin) / GRID_SIZE);
        const bottom = Math.floor((newY + TANK_SIZE - margin) / GRID_SIZE);
        if (this.gridMap.length === 0) return true;
        const isWall = (x: number, y: number) => {
            if (x < 0 || x >= this.cols || y < 0 || y >= this.rows) return true;
            const cell = this.gridMap[x * this.rows + y];
            return cell === CELL_WALL || cell === CELL_DESTRUCTIBLE;
        };
        return isWall(left, top) || isWall(right, top) || isWall(left, bottom) || isWall(right, bottom);
    };

    private interpolatePlayers() {
        for (const p of this.players.values()) {
            const dist = Math.hypot(p.targetX - p.x, p.targetY - p.y);
            if (dist > 80) { p.x = p.targetX; p.y = p.targetY; }
            else {
                p.x += (p.targetX - p.x) * 0.2;
                p.y += (p.targetY - p.y) * 0.2;
            }
        }
    }

    private updateCamera() {
        if (this.gamePhase !== 'playing') {
            this.camera.x = Math.max(0, (WORLD_WIDTH - this.canvas.width) / 2);
            this.camera.y = Math.max(0, (WORLD_HEIGHT - this.canvas.height) / 2);
            return;
        }
        const me = this.players.get(this.myId);
        if (!me) return;
        const vw = this.canvas.width, vh = this.canvas.height;
        this.camera.x = Math.max(0, Math.min(me.x + TANK_SIZE / 2 - vw / 2, WORLD_WIDTH - vw));
        this.camera.y = Math.max(0, Math.min(me.y + TANK_SIZE / 2 - vh / 2, WORLD_HEIGHT - vh));
    }

    private worldToScreen(wx: number, wy: number) {
        return { x: wx - this.camera.x, y: wy - this.camera.y };
    }

    private drawMaze(layer: 'walls' | 'bushes') {
        for (let x = 0; x < this.cols; x++) {
            for (let y = 0; y < this.rows; y++) {
                const cell = this.gridMap[x * this.rows + y];
                if (cell === CELL_EMPTY || (layer === 'walls' && cell === CELL_BUSH) || (layer === 'bushes' && cell !== CELL_BUSH)) continue;
                const { x: sx, y: sy } = this.worldToScreen(x * GRID_SIZE, y * GRID_SIZE);
                if (sx < -GRID_SIZE || sx > this.canvas.width || sy < -GRID_SIZE || sy > this.canvas.height) continue;
                this.ctx.fillStyle = cell === CELL_WALL ? '#424949' : cell === CELL_DESTRUCTIBLE ? '#8B4513' : 'rgba(39, 174, 96, 0.85)';
                this.ctx.fillRect(sx, sy, GRID_SIZE, GRID_SIZE);
                if (layer === 'walls') {
                    this.ctx.strokeStyle = '#050505';
                    this.ctx.strokeRect(sx, sy, GRID_SIZE, GRID_SIZE);
                }
            }
        }
    }

    private drawPickups() {
        for (const pk of this.pickups) {
            const { x, y } = this.worldToScreen(pk.x, pk.y);
            if (pk.type === PICKUP_SCORE) {
                this.ctx.fillStyle = '#f1c40f';
                this.ctx.beginPath();
                this.ctx.arc(x, y, 10, 0, Math.PI * 2);
                this.ctx.fill();
                this.ctx.strokeStyle = '#d4ac0d';
                this.ctx.lineWidth = 2;
                this.ctx.stroke();
            } else if (pk.type === PICKUP_HEALTH) {
                this.ctx.fillStyle = '#e74c3c';
                this.ctx.fillRect(x - 8, y - 8, 16, 16);
                this.ctx.fillStyle = '#fff';
                this.ctx.fillRect(x - 2, y - 6, 4, 12);
                this.ctx.fillRect(x - 6, y - 2, 12, 4);
            }
        }
    }

    private drawBullets() {
        this.ctx.fillStyle = '#f1c40f';
        for (const b of this.bullets) {
            const { x, y } = this.worldToScreen(b.x, b.y);
            this.ctx.beginPath();
            this.ctx.arc(x, y, 6, 0, Math.PI * 2);
            this.ctx.fill();
        }
    }

    private drawPlayers() {
        for (const [id, p] of this.players) {
            const { x, y } = this.worldToScreen(p.x, p.y);
            const cx = x + TANK_SIZE / 2;

            this.ctx.font = '11px Segoe UI, sans-serif';
            this.ctx.textAlign = 'center';
            this.ctx.fillStyle = id === this.myId ? '#FF5733' : '#ecf0f1';
            this.ctx.fillText(p.nickname, cx, y - 6);

            this.ctx.save();
            this.ctx.translate(cx, y + TANK_SIZE / 2);
            this.ctx.rotate((p.angle || 0) * (Math.PI / 2));
            this.ctx.fillStyle = id === this.myId ? '#FF5733' : '#3498db';
            this.ctx.fillRect(-TANK_SIZE / 2, -TANK_SIZE / 2, TANK_SIZE, TANK_SIZE);
            this.ctx.fillStyle = '#7f8c8d';
            this.ctx.fillRect(0, -6, TANK_SIZE / 2 + 15, 12);
            if (id === this.myId) {
                this.ctx.strokeStyle = '#ffffff';
                this.ctx.lineWidth = 2;
                this.ctx.strokeRect(-TANK_SIZE / 2, -TANK_SIZE / 2, TANK_SIZE, TANK_SIZE);
            }
            this.ctx.restore();
        }
    }

    private drawHud() {
        if (this.gamePhase !== 'playing') return;
        const me = this.players.get(this.myId);
        const hp = Math.max(0, me?.health ?? 100);
        const maxHp = Math.max(1, me?.maxHealth ?? 100);
        const pad = 16;
        const boxW = 200;
        const boxH = 52;
        const boxY = this.canvas.height - boxH - pad;
        const barW = boxW - 24;
        const barH = 14;

        this.ctx.fillStyle = 'rgba(0, 0, 0, 0.65)';
        this.ctx.fillRect(pad, boxY, boxW, boxH);
        this.ctx.strokeStyle = 'rgba(255, 255, 255, 0.15)';
        this.ctx.strokeRect(pad, boxY, boxW, boxH);

        this.ctx.fillStyle = '#922b21';
        this.ctx.fillRect(pad + 12, boxY + 10, barW, barH);
        this.ctx.fillStyle = '#27ae60';
        this.ctx.fillRect(pad + 12, boxY + 10, barW * (hp / maxHp), barH);

        this.ctx.fillStyle = '#ffffff';
        this.ctx.font = 'bold 14px Segoe UI, sans-serif';
        this.ctx.textAlign = 'left';
        this.ctx.fillText(`Harita #${this.roomId}  ·  Skor: ${this.myScore}`, pad + 12, boxY + 42);
    }

    private drawLeaderboardPanel() {
        if (this.gamePhase !== 'playing') return;
        if (this.leaderboard.length === 0) return;
        const pad = 16;
        const rowH = 22;
        const maxRows = Math.min(8, this.leaderboard.length);
        const boxW = 210;
        const boxH = 36 + maxRows * rowH;

        this.ctx.fillStyle = 'rgba(0, 0, 0, 0.65)';
        this.ctx.fillRect(this.canvas.width - boxW - pad, pad, boxW, boxH);
        this.ctx.strokeStyle = 'rgba(255, 255, 255, 0.15)';
        this.ctx.strokeRect(this.canvas.width - boxW - pad, pad, boxW, boxH);

        this.ctx.fillStyle = '#FF5733';
        this.ctx.font = 'bold 13px Segoe UI, sans-serif';
        this.ctx.textAlign = 'left';
        this.ctx.fillText('LEADERBOARD', this.canvas.width - boxW - pad + 12, pad + 22);

        this.ctx.font = '13px Segoe UI, sans-serif';
        for (let i = 0; i < maxRows; i++) {
            const e = this.leaderboard[i];
            const y = pad + 40 + i * rowH;
            this.ctx.fillStyle = e.id === this.myId ? '#FF5733' : '#ddd';
            this.ctx.fillText(`${i + 1}. ${e.nickname}`.slice(0, 16), this.canvas.width - boxW - pad + 12, y);
            this.ctx.textAlign = 'right';
            this.ctx.fillText(String(e.score), this.canvas.width - pad - 12, y);
            this.ctx.textAlign = 'left';
        }
    }

    private render() {
        if (this.gamePhase === 'playing') this.interpolatePlayers();
        this.updateCamera();

        this.ctx.fillStyle = '#050505';
        this.ctx.fillRect(0, 0, this.canvas.width, this.canvas.height);

        this.drawMaze('walls');
        if (this.gamePhase === 'playing') {
            this.drawPickups();
            this.drawPlayers();
            this.drawBullets();
        }
        this.drawMaze('bushes');

        this.drawHud();
        this.drawLeaderboardPanel();

        requestAnimationFrame(() => this.render());
    }
}

new GameClient();
