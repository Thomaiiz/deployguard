import { Injectable, InjectionToken, inject, signal } from '@angular/core';
import { Observable, share, timer } from 'rxjs';
import { LiveEvent, LiveEventName, LiveMessage } from './models';

export type LiveStatus = 'connecting' | 'live' | 'offline';

/** Fábrica do EventSource. Existe para os testes trocarem por um falso. */
export const EVENT_SOURCE_FACTORY = new InjectionToken<(url: string) => EventSource>(
  'EVENT_SOURCE_FACTORY',
  { providedIn: 'root', factory: () => (url: string) => new EventSource(url) },
);

/** Depois de uma recusa (401, 503), espera isso antes de tentar de novo. */
export const LIVE_RETRY_MS = 30_000;

/** Mesmo valor de EventSource.CLOSED (o jsdom dos testes não tem EventSource). */
const CLOSED = 2;

const EVENT_NAMES: LiveEventName[] = [
  'check',
  'incident-opened',
  'incident-resolved',
  'deployment',
];

/**
 * Canal ao vivo com o servidor (Server-Sent Events).
 *
 * O EventSource do navegador mantém uma conexão HTTP aberta e recebe mensagens de texto do
 * servidor. O cookie de sessão vai junto sozinho, porque a página e a API têm a mesma origem.
 * Se a conexão cair, o próprio navegador reconecta. Se o servidor recusar (sessão expirada ou
 * limite de conexões), o EventSource desiste, e aqui tentamos de novo depois de 30 segundos.
 */
@Injectable({ providedIn: 'root' })
export class LiveEventsService {
  private readonly createSource = inject(EVENT_SOURCE_FACTORY);

  readonly status = signal<LiveStatus>('offline');

  /**
   * Uma única conexão compartilhada entre as telas (share). Quando ninguém mais escuta,
   * espera 5 s antes de fechar, para trocar de tela sem derrubar e reabrir a conexão.
   */
  readonly messages$: Observable<LiveMessage> = new Observable<LiveMessage>((subscriber) => {
    let source: EventSource | null = null;
    let retry: ReturnType<typeof setTimeout> | undefined;

    const connect = () => {
      this.status.set('connecting');
      source = this.createSource('/api/events');

      source.addEventListener('ready', () => {
        this.status.set('live');
        subscriber.next({ name: 'ready', event: null });
      });

      for (const name of EVENT_NAMES) {
        source.addEventListener(name, (message) => {
          const event = JSON.parse((message as MessageEvent<string>).data) as LiveEvent;
          subscriber.next({ name, event });
        });
      }

      source.onerror = () => {
        if (source?.readyState === CLOSED) {
          this.status.set('offline');
          source.close();
          retry = setTimeout(connect, LIVE_RETRY_MS);
        } else {
          this.status.set('connecting'); // o navegador já está reconectando
        }
      };
    };

    connect();

    return () => {
      clearTimeout(retry);
      source?.close();
      this.status.set('offline');
    };
  }).pipe(share({ resetOnRefCountZero: () => timer(5_000) }));
}
