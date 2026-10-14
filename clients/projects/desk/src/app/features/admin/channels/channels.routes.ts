import { Routes } from '@angular/router';

/** Channel administration (lazy child of /admin; guarded by admin.channels in admin.routes.ts). */
export const ROUTES: Routes = [
  { path: '', title: 'Channels', loadComponent: () => import('./channels-list.page').then((m) => m.ChannelsListPage) },
  { path: 'new', title: 'New channel', loadComponent: () => import('./channel-editor.page').then((m) => m.ChannelEditorPage) },
  { path: 'simulator', title: 'Channel simulator', loadComponent: () => import('./channel-simulator.page').then((m) => m.ChannelSimulatorPage) },
  { path: ':id', title: 'Channel', loadComponent: () => import('./channel-editor.page').then((m) => m.ChannelEditorPage) },
];
