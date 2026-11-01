export const site = {
  name: "Media Controls",
  description:
    "Music, videos, and browser tabs, at your command. Control your media from PowerToys Command Palette and its Dock.",
  repository: "https://github.com/jiripolasek/MediaControlsExtension",
  issues: "https://github.com/jiripolasek/MediaControlsExtension/issues",
  releases: "https://github.com/jiripolasek/MediaControlsExtension/releases",
  store: "https://apps.microsoft.com/detail/9N3BQ81G19K7",
  powerToys: "https://apps.microsoft.com/detail/XP89DCGQ3K6VLD",
  author: "https://jiripolasek.com",
  winget: "winget install -e --id JiriPolasek.MediaControlsforCommandPalette",
};

export const browsers = [
  {
    id: "edge",
    name: "Microsoft Edge",
    engine: "Chromium",
    storeUrl: "https://microsoftedge.microsoft.com/addons/detail/media-controls-companion/ohigjglkifnlhjlejfbdmmjmhfbnhdin",
  },
  { id: "chrome", name: "Google Chrome", engine: "Chromium", storeUrl: null },
  { id: "firefox", name: "Mozilla Firefox", engine: "Firefox", storeUrl: null },
] as const;

export function withBase(path = "") {
  return `${import.meta.env.BASE_URL.replace(/\/?$/, "/")}${path.replace(/^\/+/, "")}`;
}
