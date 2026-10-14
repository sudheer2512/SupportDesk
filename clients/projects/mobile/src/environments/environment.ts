/**
 * Mobile runtime configuration.
 * `apiOrigin` is prepended to /api/v1 and /hubs/desk. Leave it empty to use the same origin — that is what
 * `npm run start:mobile` needs (the dev server proxies /api and /hubs to http://localhost:5830).
 * For a device build set it to the SupportDesk server, e.g. 'https://desk.example.com'
 * (Android emulator → host machine: 'http://10.0.2.2:5830').
 */
export const environment = {
  apiOrigin: '',
};
