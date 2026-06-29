import { NetworkManager } from '../network/NetworkManager';

const ADJECTIVES = ['Swift', 'Iron', 'Shadow', 'Blaze', 'Storm', 'Ghost', 'Turbo', 'Neon', 'Crimson', 'Silent'];
const NOUNS = ['Tank', 'Panzer', 'Crawler', 'Striker', 'Raider', 'Hunter', 'Viper', 'Rhino', 'Bolt', 'Fang'];

export function generateRandomNickname(): string {
    const adj = ADJECTIVES[Math.floor(Math.random() * ADJECTIVES.length)];
    const noun = NOUNS[Math.floor(Math.random() * NOUNS.length)];
    const num = Math.floor(Math.random() * 90) + 10;
    return `${adj}${noun}${num}`;
}

export function readFixedNickname(buffer: ArrayBuffer, offset: number, length: number): string {
    const bytes = new Uint8Array(buffer, offset, length);
    let end = bytes.indexOf(0);
    if (end === -1) end = length;
    return new TextDecoder().decode(bytes.subarray(0, end));
}
