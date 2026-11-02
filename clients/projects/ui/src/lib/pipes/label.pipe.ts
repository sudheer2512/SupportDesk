import { Pipe, PipeTransform } from '@angular/core';
import { enumLabel } from '@sd/core';

/** `{{ 'OnHold' | sdLabel }}` → "On hold", `{{ 'Sms' | sdLabel }}` → "SMS" — readable enum values (see `enumLabel`). */
@Pipe({ name: 'sdLabel' })
export class SdLabelPipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return enumLabel(value);
  }
}
