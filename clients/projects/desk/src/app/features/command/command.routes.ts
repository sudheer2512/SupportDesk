import { Routes } from '@angular/router';
import { CommandCenterPage } from './command-center.page';

/**
 * Command Center module routes (lazy-loaded from app.routes.ts; permission-guarded there).
 * The Approvals inbox (/approvals) and the full notifications page (/notifications) also live in this
 * folder; app.routes.ts lazy-loads them as top-level routes.
 */
export const ROUTES: Routes = [{ path: '', title: 'Command Center', component: CommandCenterPage }];
