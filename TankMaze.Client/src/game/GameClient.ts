import { NetworkManager } from '../network/NetworkManager';
import { InputHandler } from './InputHandler';
import { WORLD_WIDTH, WORLD_HEIGHT, TANK_SIZE, GRID_SIZE } from './constants';

const CELL_EMPTY = 0;
const CELL_WALL = 1;
const CELL_DESTRUCTIBLE = 2;
const CELL_BUSH = 3;

type Player = { x: number; y: number; targetX: number; targetY: number; angle: number };

export class GameClient {
    private canvas: HTMLCanvasElement;
    private ctx: CanvasRenderingContext2D;
    private players: Map<number, Player> = new Map();
    private bullets: { x: number, y: number }[] = [];
    private myId = 0;
    private camera = { x: 0, y: 0 };
    private cols = WORLD_WIDTH / GRID_SIZE;
    private rows = WORLD_HEIGHT / GRID_SIZE;
    private gridMap: Uint8Array = new Uint8Array(this.cols * this.rows);

    constructor() {
        this.canvas = document.getElementById('gameCanvas') as HTMLCanvasElement;
        this.ctx = this.canvas.getContext('2d')!;
        this.resize();
        window.addEventListener('resize', () => this.resize());
        const net = new NetworkManager((data) => this.processPacket(data));
        new InputHandler(net, () => this.myId, this.players, this.isColliding);
        this.render();
    }

    private resize() { this.canvas.width = window.innerWidth; this.canvas.height = window.innerHeight; }

    private processPacket(buffer: ArrayBuffer) {
        const data = new DataView(buffer);
        const type = data.getUint8(0);
        if (type === 0x03) { this.myId = data.getUint16(1, true); return; }
        if (type === 0x04) { this.gridMap.set(new Uint8Array(buffer, 1)); return; }
        if (type !== 0x02) return;

        this.myId = data.getUint16(1, true);
        const playerCount = data.getUint16(3, true);
        let offset = 5;

        const visibleIds = new Set<number>();
        for (let i = 0; i < playerCount; i++) {
            const id = data.getUint16(offset, true);
            const x = data.getUint16(offset + 2, true);
            const y = data.getUint16(offset + 4, true);
            const angle = data.getUint8(offset + 6);
            visibleIds.add(id);

            const p = this.players.get(id);
            if (p) { p.targetX = x; p.targetY = y; p.angle = angle; }
            else { this.players.set(id, { x, y, targetX: x, targetY: y, angle }); }
            offset += 7;
        }
        for (const id of this.players.keys()) if (!visibleIds.has(id)) this.players.delete(id);

        if (offset + 2 <= data.byteLength) {
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
        }
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
    }

    private interpolatePlayers() {
        for (const p of this.players.values()) {
            p.x += (p.targetX - p.x) * 0.2;
            p.y += (p.targetY - p.y) * 0.2;
        }
    }

    private updateCamera() {
        const me = this.players.get(this.myId);
        if (!me) return;
        const vw = this.canvas.width;
        const vh = this.canvas.height;
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
                if (layer === 'walls') { this.ctx.strokeStyle = '#050505'; this.ctx.strokeRect(sx, sy, GRID_SIZE, GRID_SIZE); }
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
            this.ctx.save();
            this.ctx.translate(x + TANK_SIZE / 2, y + TANK_SIZE / 2);
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

    private render() {
        this.interpolatePlayers();
        this.updateCamera();
        this.ctx.fillStyle = '#050505'; 
        this.ctx.fillRect(0, 0, this.canvas.width, this.canvas.height);
        
        this.drawMaze('walls');
        this.drawPlayers();
        this.drawBullets();
        this.drawMaze('bushes');
        
        requestAnimationFrame(() => this.render());
    }
}

new GameClient();