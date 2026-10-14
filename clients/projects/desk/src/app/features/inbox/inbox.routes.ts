import { Routes } from '@angular/router';

/** Inbox module routes (lazy-loaded from app.routes.ts; permission-guarded there). The selected conversation is `?c=`. */
export const ROUTES: Routes = [
  { path: '', title: 'Inbox', loadComponent: () => import('./inbox.page').then((m) => m.InboxPage) },
  { path: 'chat', title: 'Live chat', loadComponent: () => import('./chat-console.page').then((m) => m.ChatConsolePage) },
];
