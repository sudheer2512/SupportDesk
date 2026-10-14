import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  ErrorHandler,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import { TitleStrategy, provideRouter, withComponentInputBinding, withInMemoryScrolling } from '@angular/router';
import { AuthService, SD_AUTH_SESSION, ThemeService, authInterceptor, errorToastInterceptor, provideSdCore } from '@sd/core';
import { ToastService } from '@sd/ui';
import { routes } from './app.routes';
import { DeskErrorHandler } from './core/desk-error-handler';
import { DeskTitleStrategy } from './core/desk-title-strategy';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    provideRouter(routes, withComponentInputBinding(), withInMemoryScrolling({ scrollPositionRestoration: 'top' })),
    // XHR backend (no withFetch()): FilesApi upload progress needs upload events.
    // Order matters: the toast interceptor wraps the auth interceptor so it sees the final outcome of a refresh+retry.
    provideHttpClient(withInterceptors([errorToastInterceptor, authInterceptor])),
    provideSdCore({ apiBaseUrl: '/api/v1', hubUrl: '/hubs/desk', client: 'web', loginPath: '/login', forbiddenPath: '/forbidden' }),
    { provide: SD_AUTH_SESSION, useExisting: AuthService },
    { provide: ErrorHandler, useClass: DeskErrorHandler },
    { provide: TitleStrategy, useClass: DeskTitleStrategy },
    provideAppInitializer(() => {
      inject(ThemeService); // applies the saved light/dark preference before first paint
      inject(ToastService); // starts relaying @sd/core ToastBridge messages
      void inject(AuthService).ready(); // restores the session from the refresh cookie (guards await it)
    }),
  ],
};
