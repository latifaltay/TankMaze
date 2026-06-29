import { NetworkManager } from '../network/NetworkManager';

export class InputHandler {
    private keys: number = 0;

    constructor(
        private network: NetworkManager,
        private getMyId: () => number,
        private players: Map<number, { targetX: number; targetY: number; x: number; y: number }>,
        private isColliding: (x: number, y: number) => boolean
    ) {
        window.addEventListener('keydown', (e) => {
            if (e.key === ' ') {
                e.preventDefault();
                this.network.send(new Uint8Array([0x05]));
                return;
            }
            this.handleKey(e, true);
        });
        window.addEventListener('keyup', (e) => this.handleKey(e, false));
        window.addEventListener('blur', () => this.clearKeys());

        setInterval(() => {
            if (this.keys !== 0) this.sendInput();
        }, 50);
    }

    private clearKeys() {
        if (this.keys === 0) return;
        this.keys = 0;
        this.sendInput();
    }

    private handleKey(e: KeyboardEvent, isDown: boolean) {
        let bit = 0;
        if (e.key === 'w' || e.key === 'W') bit = 0x01;
        else if (e.key === 's' || e.key === 'S') bit = 0x02;
        else if (e.key === 'a' || e.key === 'A') bit = 0x04;
        else if (e.key === 'd' || e.key === 'D') bit = 0x08;

        if (bit === 0) return;
        e.preventDefault();

        if (isDown) this.keys = bit;
        else this.keys &= ~bit;

        this.applyPrediction();
        this.sendInput();
    }

    private applyPrediction() {
        const myId = this.getMyId();
        if (myId === 0) return;

        const p = this.players.get(myId);
        if (!p) return;

        let nx = p.targetX, ny = p.targetY;
        if (this.keys & 0x01) ny -= 5;
        else if (this.keys & 0x02) ny += 5;
        else if (this.keys & 0x04) nx -= 5;
        else if (this.keys & 0x08) nx += 5;

        if (!this.isColliding(nx, ny)) {
            p.targetX = nx;
            p.targetY = ny;
        }
    }

    private sendInput() {
        this.network.send(new Uint8Array([0x01, this.keys]));
    }
}
