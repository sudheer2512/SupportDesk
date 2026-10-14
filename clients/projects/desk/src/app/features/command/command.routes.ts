import { Routes } from '@angular/router';
import { CommandHomePage } from './command-home.page';

/** Command Center module routes (lazy-loaded from app.routes.ts; permission-guarded there). */
export const ROUTES: Routes = [{ path: '', title: 'Command Center', component: CommandHomePage }];
