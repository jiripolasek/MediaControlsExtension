export type Availability = "usual" | "varies" | "uncommon";
export type Integration = "builtin" | "plugin" | "conditional";

export interface Player {
  name: string;
  url?: string;
  integration: Integration;
  features: readonly [Availability, Availability, Availability, Availability, Availability, Availability];
  notes: string;
  direct?: "itunes" | "vlc";
}

export interface PlayerGroup {
  id: string;
  title: string;
  description: string;
  players: readonly Player[];
}

export const playerFeatures = ["Play / pause", "Previous / next", "Track info", "Artwork", "Track length", "Shuffle / repeat"] as const;

export const availabilityLabels: Record<Availability, string> = {
  usual: "Usually",
  varies: "Varies",
  uncommon: "Not usually",
};

export const integrationLabels: Record<Integration, string> = {
  builtin: "Built in",
  plugin: "Plugin",
  conditional: "Site-dependent",
};

// Adapted from docs/user/GSMTC-Compatibility.md in the application repository.
export const playerGroups: readonly PlayerGroup[] = [
  {
    id: "music",
    title: "Music players",
    description: "Music apps, from everyday favorites to players with community integrations.",
    players: [
      {"name":"Spotify for Windows","integration":"builtin","features":["usual","usual","usual","usual","usual","usual"],"notes":"Availability can still vary by account, device, and current content."},
      {"name":"Apple Music for Windows","integration":"builtin","features":["usual","usual","usual","usual","varies","varies"],"notes":"If the session does not appear, start playback once and check that Windows background/media-key integration is allowed for the app."},
      {"name":"Media Player","integration":"builtin","features":["usual","varies","usual","varies","usual","varies"],"notes":"Previous/next and playlist modes depend on the active queue and content type."},
      {"name":"foobar2000 1.5.1 or later","url":"https://www.foobar2000.org/","integration":"builtin","features":["usual","usual","usual","usual","uncommon","uncommon"],"notes":"Windows media-control integration was re-enabled by default in [foobar2000 1.5.1](https://www.foobar2000.org/changelog)."},
      {"name":"Dopamine","url":"https://github.com/digimezzo/dopamine-windows","integration":"builtin","features":["usual","usual","usual","usual","usual","usual"],"notes":"Support can vary between major Dopamine versions."},
      {"name":"MediaMonkey","url":"https://www.mediamonkey.com/","integration":"builtin","features":["usual","usual","usual","uncommon","uncommon","uncommon"],"notes":"App identity is generally available even when richer media properties are not."},
      {"name":"MusicBee","integration":"plugin","features":["usual","usual","usual","usual","varies","usual"],"notes":"Requires the third-party [mb_MediaControl plugin](https://github.com/ameer1234567890/mb_MediaControl)."},
      {"name":"AIMP","integration":"plugin","features":["usual","usual","usual","usual","uncommon","uncommon"],"notes":"Requires the third-party [Windows 10 Media Control plugin](https://www.aimp.ru/?do=catalog&rec_id=1097)."},
      {"name":"iTunes","integration":"plugin","features":["usual","usual","usual","usual","uncommon","uncommon"],"notes":"Enable the direct iTunes media source in Media sources to use it without a third-party SMTC plugin.","direct":"itunes"},
    ],
  },
  {
    id: "video",
    title: "Video and media players",
    description: "Desktop video players and the separate Microsoft Store edition of VLC.",
    players: [
      {"name":"Movies & TV","integration":"builtin","features":["usual","varies","usual","varies","usual","uncommon"],"notes":"Previous/next depends on the active playlist."},
      {"name":"VLC UWP","integration":"builtin","features":["usual","usual","usual","usual","uncommon","uncommon"],"notes":"This is the Microsoft Store/UWP edition, not the classic desktop application."},
      {"name":"MPC-HC","url":"https://github.com/clsid2/mpc-hc","integration":"builtin","features":["usual","usual","usual","varies","uncommon","uncommon"],"notes":"Feature availability depends on the media and playlist."},
      {"name":"MPC-BE","url":"https://sourceforge.net/projects/mpcbe/","integration":"builtin","features":["usual","usual","usual","varies","uncommon","uncommon"],"notes":"Feature availability depends on the media and playlist."},
      {"name":"Rise Media Player","url":"https://github.com/theimpactfulcompany/Rise-Media-Player","integration":"builtin","features":["usual","usual","usual","usual","usual","varies"],"notes":"Some transport actions may be reported as unavailable for particular content."},
      {"name":"VLC desktop 3.x","integration":"plugin","features":["usual","usual","usual","varies","uncommon","uncommon"],"notes":"Requires the third-party [VLC Windows 10 SMTC plugin](https://github.com/spmn/vlc-win10smtc), which targets VLC 3.0.x.","direct":"vlc"},
      {"name":"Winamp","integration":"plugin","features":["usual","usual","usual","varies","uncommon","uncommon"],"notes":"Requires the third-party [gen_smtc plugin](https://github.com/NanMetal/gen_smtc)."},
      {"name":"mpv","integration":"plugin","features":["usual","varies","usual","varies","uncommon","uncommon"],"notes":"Requires [MPV-SMTC](https://github.com/x0wllaar/MPV-SMTC) or [MPVMediaControl](https://github.com/datasone/MPVMediaControl); previous/next and artwork support differ between them."},
    ],
  },
  {
    id: "browsers",
    title: "Browsers and web players",
    description: "The browser connects media to Windows; the website provides the controls and track details.",
    players: [
      {"name":"Microsoft Edge","integration":"builtin","features":["usual","varies","varies","varies","varies","uncommon"],"notes":"Capabilities come from the active website."},
      {"name":"Google Chrome","integration":"builtin","features":["usual","varies","varies","varies","varies","uncommon"],"notes":"Capabilities come from the active website."},
      {"name":"Other Chromium-based browsers","integration":"builtin","features":["usual","varies","varies","varies","varies","uncommon"],"notes":"Includes browsers such as Brave, Opera, and Vivaldi; individual builds can differ."},
      {"name":"Firefox","integration":"builtin","features":["usual","varies","varies","varies","varies","uncommon"],"notes":"Browser settings and website implementation can affect media-control integration."},
      {"name":"YouTube and YouTube Music","integration":"conditional","features":["usual","varies","usual","usual","varies","varies"],"notes":"Appears through the browser; controls differ by site mode, queue, and account."},
      {"name":"SoundCloud web player","integration":"conditional","features":["usual","varies","usual","usual","varies","uncommon"],"notes":"Appears through the browser."},
      {"name":"Netflix, Disney+, HBO Max, Prime Video, and similar services","integration":"conditional","features":["usual","uncommon","varies","varies","varies","uncommon"],"notes":"Appears through the browser or installed web app. Metadata and artwork can vary by service."},
    ],
  },
];
