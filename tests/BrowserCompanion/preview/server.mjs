import { createServer } from "node:http";
import { readFile } from "node:fs/promises";

const extension = new URL("../../../src/BrowserCompanion/extension/", import.meta.url);
const sources = new Map(["popup.html", "popup.css", "popup.js", "grouping.mjs", "routing.mjs", "icons/icon-48.png"]
    .map((name) => [`/${name}`, new URL(name, extension)]));
sources.set("/preview-state.js", new URL("state.js", import.meta.url));
const types = { html: "text/html", css: "text/css", js: "text/javascript", mjs: "text/javascript", png: "image/png" };
const covers = {
    "/cover-one.svg": '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 160 160"><rect width="160" height="160" fill="#154d53"/><circle cx="101" cy="64" r="53" fill="#a8cac0"/><circle cx="72" cy="110" r="57" fill="#28696b"/><path d="M0 135L160 82v78H0z" fill="#113940"/></svg>',
    "/cover-two.svg": '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 160 160"><rect width="160" height="160" fill="#edc393"/><path d="M32 160V40h42v120M85 160V18h42v142" fill="#963d32"/><path d="M32 58h42M85 36h42M32 92h42M85 72h42" stroke="#edc393" stroke-width="6"/></svg>'
};

createServer(async (request, response) => {
    const pathname = new URL(request.url, "http://127.0.0.1").pathname;
    if (pathname === "/favicon.ico") { response.writeHead(204); response.end(); return; }
    const path = pathname === "/" ? "/popup.html" : pathname;
    if (covers[path]) {
        response.writeHead(200, { "Content-Type": "image/svg+xml", "Cache-Control": "no-store" });
        response.end(covers[path]);
        return;
    }
    const source = sources.get(path);
    if (!source) { response.writeHead(404); response.end(); return; }
    try {
        let body = await readFile(source);
        if (path === "/popup.html") body = body.toString().replace('<script src="popup.js"', '<script src="preview-state.js"></script>\n  <script src="popup.js"');
        response.writeHead(200, { "Content-Type": types[path.split(".").at(-1)], "Cache-Control": "no-store" });
        response.end(body);
    } catch {
        response.writeHead(500);
        response.end("Preview source unavailable.");
    }
}).listen(8766, "127.0.0.1", () => console.log("Popup preview: http://127.0.0.1:8766 (?state=empty|disconnected|sleeping|long|many)"));
