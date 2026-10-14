import type { CapacitorConfig } from '@capacitor/cli';

/**
 * SupportDesk mobile shell (clients/projects/mobile — Ionic + Capacitor).
 * Build the web assets first (`npm run build:mobile` → dist/mobile/browser), then `npx cap add android|ios`
 * and `npx cap sync`. Native folders are intentionally not generated yet.
 * The API origin is configured in projects/mobile/src/environments/environment.ts (same origin by default).
 */
const config: CapacitorConfig = {
  appId: 'com.supportdesk.app',
  appName: 'SupportDesk',
  webDir: 'dist/mobile/browser',
  android: {
    // Allows calls to an http:// API on the LAN during development.
    allowMixedContent: true,
  },
  ios: {
    contentInset: 'never',
  },
  plugins: {
    Keyboard: {
      resizeOnFullScreen: true,
    },
  },
};

export default config;
