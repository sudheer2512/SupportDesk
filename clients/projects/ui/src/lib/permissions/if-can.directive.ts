import { Directive, TemplateRef, ViewContainerRef, effect, inject, input } from '@angular/core';
import { MeStore, Permission } from '@sd/core';

/**
 * Structural permission check: `<button *sdIfCan="'tickets.assign'">Assign</button>`,
 * `<section *sdIfCan="['admin.users', 'admin.roles']">` (any of). Purely cosmetic — the API enforces.
 */
@Directive({ selector: '[sdIfCan]' })
export class SdIfCan {
  readonly permissions = input.required<Permission | string | readonly (Permission | string)[]>({ alias: 'sdIfCan' });

  private readonly me = inject(MeStore);
  private readonly template = inject(TemplateRef<unknown>);
  private readonly vcr = inject(ViewContainerRef);
  private shown = false;

  constructor() {
    effect(() => {
      const p = this.permissions();
      const allowed = this.me.canAny(Array.isArray(p) ? p : [p as string]);
      if (allowed && !this.shown) {
        this.vcr.createEmbeddedView(this.template);
        this.shown = true;
      } else if (!allowed && this.shown) {
        this.vcr.clear();
        this.shown = false;
      }
    });
  }
}
