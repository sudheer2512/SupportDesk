import { Routes } from '@angular/router';

/** Engineering module routes (lazy-loaded from app.routes.ts; guarded by engineering.view / engineering.manage). */
export const ROUTES: Routes = [
  { path: '', title: 'Engineering board', loadComponent: () => import('./board/engineering-board.page').then((m) => m.EngineeringBoardPage) },
];
