import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import qrcode from 'qrcode-generator';

/**
 * `<sd-qr-code [value]="otpAuthUri" [size]="184" label="Authenticator QR code" />` — QR code rendered
 * offline as a single SVG path (qrcode-generator, error correction M). Dark modules on a white quiet zone
 * so scanners work in dark mode too.
 */
@Component({
  selector: 'sd-qr-code',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (matrix(); as m) {
      <svg
        [attr.viewBox]="'0 0 ' + (m.count + 8) + ' ' + (m.count + 8)"
        [attr.width]="size()"
        [attr.height]="size()"
        role="img"
        [attr.aria-label]="label()"
        shape-rendering="crispEdges"
      >
        <rect [attr.width]="m.count + 8" [attr.height]="m.count + 8" fill="#ffffff" />
        <path [attr.d]="m.path" fill="#111111" />
      </svg>
    }
  `,
  styles: `
    :host {
      display: inline-flex;
      padding: 4px;
      border-radius: var(--sd-radius-md);
      background: #ffffff;
    }
  `,
})
export class SdQrCode {
  readonly value = input.required<string>();
  readonly size = input(184);
  readonly label = input('QR code');

  protected readonly matrix = computed(() => {
    const value = this.value();
    if (!value) return null;
    const qr = qrcode(0, 'M');
    qr.addData(value);
    qr.make();
    const count = qr.getModuleCount();
    let path = '';
    for (let r = 0; r < count; r++) {
      for (let c = 0; c < count; c++) {
        if (qr.isDark(r, c)) path += `M${c + 4},${r + 4}h1v1h-1z`;
      }
    }
    return { count, path };
  });
}
