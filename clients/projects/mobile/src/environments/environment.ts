/**
 * Mobile runtime configuration.
 * `apiOrigin` is prepended to /api/v1, /hubs/desk and /hubs/portal. Leave it empty to use the same origin — that is
 * what `npm run start:mobile` needs (the dev server proxies /api and /hubs to the API).
 * For a device build set it to the SupportDesk server, e.g. 'https://desk.example.com'
 * (Android emulator → host machine: 'http://10.0.2.2:5830') and add the app origin to the API's `Cors:Origins`
 * (`capacitor://localhost` for iOS, `https://localhost` for Android). See docs/MOBILE.md.
 * `appVersion` is reported with push-device registrations.
 */
export const environment = {
  apiOrigin: '',
  appVersion: '1.0.0',
};
