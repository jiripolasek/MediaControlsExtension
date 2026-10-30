import http from "node:http";
import { readFile } from "node:fs/promises";
import { deflateSync } from "node:zlib";

function png(rgb) {
    const chunk = (name, data) => {
        const body = Buffer.concat([Buffer.from(name), data]);
        let crc = 0xffffffff;
        for (const byte of body) {
            crc ^= byte;
            for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (crc & 1 ? 0xedb88320 : 0);
        }
        const size = Buffer.alloc(4); size.writeUInt32BE(data.length);
        const check = Buffer.alloc(4); check.writeUInt32BE((crc ^ 0xffffffff) >>> 0);
        return Buffer.concat([size, body, check]);
    };
    const header = Buffer.alloc(13);
    header.writeUInt32BE(96, 0); header.writeUInt32BE(96, 4); header[8] = 8; header[9] = 2;
    const pixels = Buffer.alloc((96 * 3 + 1) * 96);
    for (let y = 0; y < 96; y++) for (let x = 0; x < 96; x++) {
        const color = x > 32 && x < 64 && y > 24 && y < 72 ? [255, 255, 255] : rgb;
        for (let c = 0; c < 3; c++) pixels[y * 289 + 1 + x * 3 + c] = color[c];
    }
    return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk("IHDR", header), chunk("IDAT", deflateSync(pixels)), chunk("IEND", Buffer.alloc(0))]);
}

const images = new Map([["/cover-0.png", png([177, 48, 83])], ["/cover-1.png", png([77, 63, 174])], ["/favicon.png", png([22, 137, 207])]]);

const rate = 16000;
const samples = rate * 12;
const wave = Buffer.alloc(44 + samples * 2);
wave.write("RIFF", 0); wave.writeUInt32LE(wave.length - 8, 4); wave.write("WAVEfmt ", 8);
wave.writeUInt32LE(16, 16); wave.writeUInt16LE(1, 20); wave.writeUInt16LE(1, 22);
wave.writeUInt32LE(rate, 24); wave.writeUInt32LE(rate * 2, 28);
wave.writeUInt16LE(2, 32); wave.writeUInt16LE(16, 34);
wave.write("data", 36); wave.writeUInt32LE(samples * 2, 40);
for (let i = 0; i < samples; i++) wave.writeInt16LE(Math.round(Math.sin(i * 2 * Math.PI * 220 / rate) * 600), 44 + i * 2);
const html = await readFile(new URL("index.html", import.meta.url));
http.createServer((request, response) => {
    const audio = request.url === "/tone.wav";
    const image = images.get(request.url);
    response.writeHead(200, { "Content-Type": image ? "image/png" : audio ? "audio/wav" : "text/html; charset=utf-8", "Cache-Control": "no-store" });
    response.end(image || (audio ? wave : html));
}).listen(8765, "127.0.0.1", () => console.log("Fixture: http://127.0.0.1:8765 (open twice, in different windows)"));
