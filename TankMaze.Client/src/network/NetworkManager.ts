export class NetworkManager {
    private socket: WebSocket;

    constructor(
        private onMessage: (data: ArrayBuffer) => void,
        onOpen?: () => void
    ) {
        this.socket = new WebSocket('ws://localhost:5157/ws');
        this.socket.binaryType = 'arraybuffer';
        this.socket.onmessage = (e) => this.onMessage(e.data as ArrayBuffer);
        this.socket.onopen = () => onOpen?.();
    }

    send(data: Uint8Array) {
        if (this.socket.readyState === WebSocket.OPEN) {
            // TypeScript'in SharedArrayBuffer tip kısıtlamasını baypas etmek için
            // as unknown as BufferSource diyerek derleyiciyi susturuyoruz.
            this.socket.send(data as unknown as BufferSource);
        }
    }
}