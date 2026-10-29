import { defineConfig } from "astro/config";

export default defineConfig({
  site: process.env.SITE_URL ?? "https://jiripolasek.github.io",
  base: process.env.BASE_PATH ?? "/MediaControlsExtension",
  output: "static",
  trailingSlash: "always",
  devToolbar: { enabled: false },
});
