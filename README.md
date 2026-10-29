# DeployGuard

Plataforma de monitoramento de aplicações e investigação de incidentes.

Funcionalidades atuais:

- **Etapa 1:** cadastrar aplicações com uma URL de health check, verificá-las periodicamente de
  forma segura, registrar disponibilidade, latência e erros, expor o histórico por uma API REST e
  mostrar o estado de cada aplicação num dashboard.
- **Etapa 2:** abrir e fechar **incidentes** automaticamente quando uma aplicação cai e volta, e
  **alertar** o time por webhook (Slack, Discord ou JSON genérico), com entrega confiável.
- **Etapa 3:** **login** com e-mail e senha e dois papéis: **Admin** (cadastra aplicações e
  usuários) e **Viewer** (só acompanha). Toda a API exige login.
- **Etapa 4:** **registro de deploys**. O pipeline de CI avisa cada versão que vai para produção
  (com uma chave de API), e o DeployGuard mostra os deploys no gráfico e aponta o **deploy
  suspeito** quando um incidente começa logo depois de um deploy, inclusive no alerta do Slack.
- **Etapa 5:** **painel em tempo real**. O dashboard recebe avisos do servidor por
  **Server-Sent Events** e se atualiza em menos de um segundo quando uma verificação termina, um
  incidente abre ou fecha, ou um deploy é registrado.
- **Etapa 6:** **várias instâncias da API**. O Compose sobe duas cópias atrás do nginx. Elas dividem
  as verificações e os alertas pelo banco (`FOR UPDATE SKIP LOCKED`), sem verificar nada em dobro,
  e os avisos ao vivo chegam a todas pelo `LISTEN/NOTIFY` do PostgreSQL.
- **Etapa 7:** **conta e segurança do login**. Cada usuário troca a própria senha (e as outras
  sessões caem na hora), a conta é bloqueada por 15 minutos depois de 5 senhas erradas seguidas, e
  um **registro de auditoria** mostra ao Admin quem fez o quê, quando e de qual IP.
- **Etapa 8:** **página de status pública** em `/status`, aberta sem login. O Admin escolhe quais
  aplicações aparecem, e o visitante vê só o nome, a situação e uma barra por dia com a
  disponibilidade dos últimos 30 dias. URLs, erros e latências ficam de fora.

| Camada | Tecnologia |
|---|---|
| API e agendador | ASP.NET Core (.NET 10 LTS), minimal APIs, EF Core 10 |
| Banco | PostgreSQL 17 (Npgsql) |
| Dashboard | Angular 22 (standalone components, signals, zoneless) |
| Testes | xUnit, WebApplicationFactory, Testcontainers, Vitest |
| Infra | Docker Compose, nginx, GitHub Actions, Dependabot |

> Fora do escopo por enquanto (visão futura): AWS CloudWatch, OpenTelemetry, investigação
> assistida por IA e integração com Azure.

---

## Sumário

- [Como rodar](#como-rodar)
- [Arquitetura](#arquitetura)
- [Decisões de arquitetura](#decisões-de-arquitetura)
- [Segurança](#segurança)
- [API REST](#api-rest)
- [Configuração](#configuração)
- [Testes e CI](#testes-e-ci)
- [Estrutura de pastas](#estrutura-de-pastas)
- [Limitações conhecidas e próximos passos](#limitações-conhecidas-e-próximos-passos)

Para um guia de estudo com os conceitos e perguntas de entrevista, veja
[`docs/guia-de-estudo.md`](docs/guia-de-estudo.md).

---

## Como rodar

### Opção 1: Docker Compose (tudo junto)

Pré-requisito: Docker com Compose v2.

```bash
cp .env.example .env          # edite: POSTGRES_PASSWORD, AUTH_JWT_KEY, ADMIN_EMAIL, ADMIN_PASSWORD
docker compose up --build
```

Para gerar a chave de assinatura dos tokens: `openssl rand -base64 48`. O `ADMIN_EMAIL` e o
`ADMIN_PASSWORD` criam o primeiro administrador na primeira vez que a API sobe (senha com 12 ou
mais caracteres, sem o seu e-mail). Depois disso, outros usuários são criados pela tela
**Usuários** do dashboard.

Abra <http://localhost:8080> e entre com o admin. Para ver o monitoramento funcionando sem
depender da internet, cadastre a aplicação de demonstração que sobe junto:

| Nome | URL | Resultado esperado |
|---|---|---|
| Demo | `http://demo-app:8080/health` | Operacional |
| Demo quebrado | `http://demo-app:8080/broken` | Fora do ar (HTTP 503) |
| Teste de SSRF | `http://169.254.169.254:8080/` | Rejeitado no cadastro |

O Compose sobe **duas réplicas** da API. Para ver as duas dividindo o trabalho:

```bash
docker compose ps                                      # api-1 e api-2 rodando
docker compose logs -f api | grep "health checks"      # rodadas feitas por api-1 e por api-2
```

Para subir uma só, use `API_REPLICAS=1` no `.env`.

Para receber alertas, coloque no `.env` a URL de um
[Incoming Webhook do Slack](https://api.slack.com/messaging/webhooks) ou de um webhook do Discord
(`ALERT_WEBHOOK_URL` e `ALERT_WEBHOOK_FORMAT`). Sem isso, os incidentes aparecem no dashboard, mas
nenhuma mensagem é enviada.

Só o dashboard é publicado, e apenas em `127.0.0.1:8080`. API e banco ficam acessíveis só pela
rede interna do Compose.

### Opção 2: desenvolvimento local

Pré-requisitos: .NET SDK 10, Node.js 24 e um PostgreSQL rodando.

```bash
# 1. Segredos fora do repositório (ficam no perfil do seu usuário)
cd backend/src/DeployGuard.Api
dotnet user-secrets set "ConnectionStrings:DeployGuard" \
  "Host=localhost;Database=deployguard;Username=deployguard;Password=<sua-senha>"
dotnet user-secrets set "Auth:JwtSigningKey" "$(openssl rand -base64 48)"
dotnet user-secrets set "Auth:BootstrapAdminEmail" "voce@exemplo.com"
dotnet user-secrets set "Auth:BootstrapAdminPassword" "<uma senha com 12+ caracteres>"

# 2. API em http://localhost:5080 (aplica as migrations ao iniciar em Development)
dotnet run

# 3. Dashboard em http://localhost:4200 (o proxy do ng serve encaminha /api para a 5080)
cd ../../../frontend
npm ci
npm start
```

Em Development a allowlist aceita qualquer host público (`*`) e HTTP. Destinos privados
continuam bloqueados.

---

## Arquitetura

```mermaid
flowchart LR
    user([Navegador]) -->|HTTP :8080| nginx

    subgraph compose[Docker Compose]
        nginx[web: nginx<br/>Angular estático + proxy /api]
        subgraph api[api: ASP.NET Core, 2 réplicas]
            endpoints[Endpoints REST<br/>+ rate limiting]
            scheduler[HealthCheckScheduler<br/>BackgroundService]
            checker[HealthChecker<br/>+ SafeSocketConnector]
            dispatcher[AlertDispatcher<br/>BackgroundService]
            hub[LiveEventHub<br/>+ LISTEN/NOTIFY]
        end
        db[(PostgreSQL)]
        demo[demo-app]
    end

    nginx -->|/api| endpoints
    nginx -.->|/api/events SSE| hub
    scheduler -.->|publica| hub
    endpoints -.->|publica deploy| hub
    endpoints --> db
    scheduler --> db
    scheduler --> checker
    checker -->|GET health check| demo
    checker -->|GET health check| internet((Hosts autorizados<br/>na internet))
    dispatcher --> db
    dispatcher -->|POST webhook| chat((Slack / Discord))
```

### Fluxo de uma verificação

1. A cada 5 segundos o `HealthCheckScheduler` busca as aplicações habilitadas com
   `NextCheckAt <= agora`.
2. As verificações rodam em paralelo, até `MaxConcurrentChecks` ao mesmo tempo.
3. Para cada uma, o `HealthChecker` valida a URL de novo (a allowlist pode ter mudado) e faz um
   `GET` com timeout total.
4. Na hora de abrir a conexão TCP, o `SafeSocketConnector` resolve o DNS, confere se **todos** os
   IPs são públicos e conecta exatamente nesses IPs.
5. A resposta vira um `HealthCheckOutcome`: `Up` (2xx), `Degraded` (2xx lento) ou `Down` (qualquer
   outra coisa, com a categoria do erro).
6. O `IncidentTracker` decide se a verificação abre ou fecha um incidente (veja abaixo).
7. O agendador grava resultados, estado atual, incidentes e notificações pendentes numa única
   transação, e agenda a próxima verificação para `agora + intervalo`.

### Incidentes e alertas

```mermaid
stateDiagram-v2
    [*] --> Saudavel
    Saudavel --> Saudavel: falhas < limiar
    Saudavel --> IncidenteAberto: falhas consecutivas = limiar\n(enfileira alerta "caiu")
    IncidenteAberto --> IncidenteAberto: continua falhando
    IncidenteAberto --> Saudavel: primeira resposta 2xx\n(enfileira alerta "voltou")
```

- **Limiar de falhas** (`FailureThreshold`, padrão 2): uma falha isolada não vira alerta. Isso evita
  *flapping*, alertas que abrem e fecham por instabilidade momentânea.
- **Um incidente aberto por aplicação**, garantido por um índice único parcial no PostgreSQL
  (`WHERE "ResolvedAt" IS NULL`).
- **Transactional outbox:** o alerta não é enviado durante a verificação. Ele é gravado na tabela
  `alert_notifications` na mesma transação do incidente. O `AlertDispatcher` lê essa tabela a cada
  10 s e faz o `POST` no webhook. Se o Slack estiver fora do ar, o alerta não se perde nem trava as
  verificações.
- **Retentativas com backoff exponencial:** 30 s, 1 min, 2 min, 4 min... até `MaxDeliveryAttempts`.
- **Entrega "pelo menos uma vez":** se o processo cair entre enviar e gravar, a mensagem é
  reenviada. Um alerta duplicado é aceitável; um perdido, não.

### Deploys e deploy suspeito

```mermaid
sequenceDiagram
    participant CI as GitHub Actions
    participant API as DeployGuard API
    participant S as Agendador
    participant Slack
    CI->>API: POST /deployments (Bearer dg_...) "v1.4.2"
    Note over S: 3 min depois a aplicação começa a falhar
    S->>API: abre incidente + grava alerta (outbox)
    API->>Slack: "Loja FORA DO AR... Último deploy: v1.4.2, 3 min antes."
```

A primeira pergunta de quem está de plantão é "o que mudou?". Na maioria das vezes a resposta é
um deploy. O DeployGuard procura o deploy mais recente da aplicação até
`CorrelationWindowMinutes` (30) antes do início do incidente e o mostra na API, no dashboard e no
alerta. É uma **pista** para a investigação, não uma prova de causa. A busca usa o índice
`(ApplicationId, DeployedAt)` e, na listagem de incidentes, vira uma subconsulta na mesma ida ao
banco.

### Painel em tempo real

```mermaid
sequenceDiagram
    participant B as Navegador (EventSource)
    participant API as GET /api/events
    participant H as LiveEventHub
    participant S as Agendador
    B->>API: conecta (cookie de sessão)
    API-->>B: event: ready
    S->>H: Publish(check, incident-opened)
    H-->>API: fila desta conexão
    API-->>B: event: check / event: incident-opened
    B->>B: espera 500 ms juntando avisos
    B->>API: GET /api/applications (dados completos)
    Note over API,B: sem eventos por 15 s: event: ping<br/>depois de 5 min o servidor fecha e o navegador reconecta
```

- **Server-Sent Events (SSE)** é uma resposta HTTP comum (`text/event-stream`) que fica aberta e
  recebe mensagens de texto do servidor. É só servidor → navegador, que é exatamente o caso do
  dashboard. O `EventSource` do navegador reconecta sozinho e manda o cookie de sessão, então não
  precisa de biblioteca, protocolo novo nem token na URL (como seria com WebSocket).
- **O evento é só um aviso.** Ele diz "a aplicação X mudou", e a tela busca os dados na API, como
  já fazia. Não existe um segundo estado montado a partir de eventos que possa divergir, e perder
  um aviso não deixa a tela errada: o próximo aviso ou a atualização de segurança de 1 minuto
  corrige.
- **`LiveEventHub`**: cada conexão tem sua própria fila (`Channel`) limitada a 100 eventos. Se o
  cliente for lento, os mais antigos são descartados (`DropOldest`), e quem publica nunca espera.
  Os avisos passam pelo PostgreSQL (`NOTIFY`) dentro da mesma transação dos dados, então nunca
  chegam antes do dado (veja "Várias instâncias", abaixo).
- **Proteções**: exige login como o resto da API (uma chave de CI não serve), máximo de
  `MaxConnections` conexões (acima disso, `503` e a tela segue com a atualização de 1 minuto),
  `ping` a cada 15 s para proxies não derrubarem a conexão parada, e duração máxima de 5 min: cada
  reconexão passa pela autenticação de novo, então quem foi removido para de receber.
- **No frontend**, uma conexão é compartilhada entre as telas (`share`), e uma rajada de avisos (uma
  rodada de verificações) vira uma busca só (`auditTime`). O nginx tem um `location` próprio para
  `/api/events` com `proxy_buffering off`, senão os eventos ficariam presos no buffer.

### Várias instâncias

O `docker-compose.yml` sobe **duas réplicas** da API (`API_REPLICAS`, padrão 2). O nginx distribui
as requisições entre elas, e qualquer uma atende qualquer usuário, porque o login (JWT) não guarda
nada na memória do servidor. O que precisou de cuidado foi o trabalho em segundo plano:

```mermaid
sequenceDiagram
    participant A as API réplica 1
    participant DB as PostgreSQL
    participant B as API réplica 2
    A->>DB: UPDATE ... WHERE Id IN (SELECT ... FOR UPDATE SKIP LOCKED) RETURNING *
    B->>DB: mesmo UPDATE, no mesmo instante
    DB-->>A: apps 1 a 8 (NextCheckAt = agora + prazo)
    DB-->>B: apps 9 a 16 (pulou as travadas pela réplica 1)
    A->>A: verifica 1 a 8
    A->>DB: grava resultados + NOTIFY deployguard_live (mesma transação)
    DB-->>B: notificação (B faz LISTEN)
    B-->>B: repassa para os navegadores conectados nela
```

- **Agendador e alertas: reserva com `FOR UPDATE SKIP LOCKED`.** Cada instância reserva um lote num
  único `UPDATE` atômico. Linhas que outra instância está reservando naquele instante são
  **puladas**, não esperadas. A reserva empurra `NextCheckAt` (ou `NextAttemptAt`, nos alertas)
  para o fim de um **prazo** (*lease*). Para as outras instâncias, a linha deixa de estar vencida.
  Ao terminar, a instância grava o horário certo da próxima verificação.
- **Se uma instância cair no meio**, nada fica preso: o prazo vence e outra instância pega a
  aplicação. Nenhuma trava fica aberta durante as chamadas HTTP, que podem levar segundos. O prazo
  é calculado da configuração (o pior caso do lote em dobro, mais uma folga), então não há um
  número a mais para errar.
- **Lotes pequenos** (`MaxConcurrentChecks` x 4) para várias instâncias dividirem um volume grande.
  A instância processa lotes até não sobrar nada vencido.
- **Avisos ao vivo com `LISTEN/NOTIFY`.** Quem produz um aviso faz `pg_notify` no banco. Cada
  instância mantém uma conexão fazendo `LISTEN` e repassa os avisos aos navegadores conectados
  nela. O `NOTIFY` é **transacional**: só é entregue no `COMMIT`, e um `ROLLBACK` o descarta. Assim
  o aviso nunca chega antes do dado nem anuncia algo que não foi gravado. Se a conexão cair, ela
  reconecta com espera crescente, e enquanto isso as telas seguem com a atualização de 1 minuto.
- **Subida simultânea.** As duas réplicas sobem juntas. No teste com Compose, as duas tentaram
  aplicar as migrations ao mesmo tempo e uma quebrou com `column already exists`. A correção foi
  uma **trava consultiva** do PostgreSQL (`pg_advisory_lock`): a primeira migra, e a segunda espera
  e encontra tudo aplicado. A criação do admin inicial também pode correr em paralelo; a
  restrição de e-mail único garante que só uma consegue, e a outra só registra no log.

### Página de status pública

A rota `GET /api/public/status` é a **única rota de dados sem login**. Por isso ela foi pensada
como uma vitrine, não como uma janela para dentro do sistema:

- **Publicar é opt-in.** Cada aplicação tem `IsPublic`, desligado por padrão. O Admin publica pelo
  botão na tela da aplicação, e a mudança vai para a auditoria.
- **Contrato próprio e mínimo.** A resposta usa DTOs separados (`PublicStatusResponse`) com nome,
  situação, disponibilidade e uma barra por dia. Nada de `Id`, URL, latência ou mensagem de erro,
  que revelariam a infraestrutura interna (ex.: `connect 10.0.0.5:443 timed out`). Um teste confere
  que nada disso aparece no JSON.
- **Queda só com incidente aberto.** Uma falha isolada aparece como "Instável", e "Fora do ar" só
  quando há incidente (várias falhas seguidas). A página pública não pisca vermelho a cada oscilação.
- **Cache de saída + rate limit próprio.** A disponibilidade por dia é um `GROUP BY` sobre milhares
  de resultados. Para que muitos visitantes não virem muitas consultas, a resposta fica 30 s no
  **output cache** do ASP.NET Core (e o navegador recebe `Cache-Control: public, max-age=30`). A
  rota tem cota própria de 30 requisições por minuto por IP.
- **Sem tempo real.** O canal ao vivo exige login, então a página faz polling a cada minuto.
- **Dias em UTC.** O histórico tem `StatusPage:HistoryDays` (padrão 90) limitado pela retenção
  (`Monitoring:ResultRetentionDays`, padrão 30): não dá para mostrar o que já foi apagado.

### Modelo de dados

```mermaid
erDiagram
    applications ||--o{ health_check_results : "tem"
    applications ||--o{ incidents : "tem"
    incidents ||--o{ alert_notifications : "gera"
    applications {
        uuid id PK
        varchar name
        varchar health_check_url
        int check_interval_seconds
        bool is_enabled
        timestamptz next_check_at
        timestamptz last_checked_at
        varchar last_status
        int last_latency_ms
        int consecutive_failures
    }
    incidents {
        bigint id PK
        uuid application_id FK
        timestamptz started_at
        timestamptz resolved_at
        varchar cause
        varchar summary
    }
    alert_notifications {
        bigint id PK
        bigint incident_id FK
        varchar event_type
        varchar channel
        int attempts
        timestamptz next_attempt_at
        timestamptz sent_at
        timestamptz failed_at
    }
    health_check_results {
        bigint id PK
        uuid application_id FK
        timestamptz checked_at
        varchar status
        int http_status_code
        int latency_ms
        varchar error_type
        varchar error_message
    }
```

Índices: `(is_enabled, next_check_at)` para o agendador, `(application_id, checked_at DESC)` para o
histórico e `checked_at` para a limpeza por retenção.

---

## Decisões de arquitetura

**Monólito modular, um projeto de API.** Toda a lógica está em `DeployGuard.Api`, organizada por
funcionalidade (`Applications/`, `Monitoring/`, `History/`). Para o tamanho atual, separar em
camadas/projetos (Clean Architecture completa) só adicionaria indireção. As fronteiras por pasta já
permitem extrair um serviço depois, se fizer sentido.

**Minimal APIs em vez de controllers.** Menos cerimônia, endpoints agrupados com `MapGroup`, e as
dependências chegam por parâmetro (injeção de dependência por método).

**Agendador dentro da API (`BackgroundService`).** Um único processo para subir e operar. Com
várias instâncias, cada uma reserva lotes diferentes com `FOR UPDATE SKIP LOCKED`, e o próprio
banco coordena quem faz o quê. Um worker separado seria o próximo passo se as verificações
começassem a disputar CPU com as requisições do dashboard.

**PostgreSQL como fila e como carteiro.** `SKIP LOCKED` faz uma tabela funcionar como fila de
trabalho, e `LISTEN/NOTIFY` entrega avisos entre processos. Para o volume do DeployGuard, isso
evita subir RabbitMQ ou Redis só para coordenar instâncias: um serviço a menos para operar,
proteger e monitorar.

**`NextCheckAt` persistido.** A pergunta "quem está vencido?" vira uma consulta indexada simples, e
o agendamento sobrevive a reinícios.

**Estado atual desnormalizado.** `LastStatus`, `LastLatencyMs` e `LastCheckedAt` ficam na própria
aplicação. O dashboard lê tudo com um `SELECT` só, sem procurar o resultado mais recente de cada
uma.

**Paralelismo sem compartilhar o `DbContext`.** O `DbContext` não é thread-safe. As verificações
rodam em paralelo só com dados em memória, e a gravação acontece depois, numa thread.

**Paginação keyset (cursor).** O histórico cresce sem parar. `OFFSET` fica mais lento a cada página
e pode pular ou repetir linhas quando dados novos chegam. Com o cursor `(CheckedAt, Id)`, cada
página começa logo depois do último item visto e usa o índice.

**Categorias de erro em vez de mensagens brutas.** Guardar `Timeout`, `DnsFailure` etc. permite
agregar erros nas estatísticas e evita gravar detalhes internos de rede vindos de exceções.

**`TimeProvider` e `IHealthChecker`.** Abstrações mínimas que existem por causa dos testes: o relógio
pode ser controlado e as chamadas HTTP podem ser substituídas.

**Angular com signals, avisos ao vivo e polling de segurança.** O estado de cada tela fica em
`signal`s e `computed`s. A busca dos dados é disparada por avisos ao vivo (SSE) ou, se o canal
estiver fora, a cada minuto. O RxJS (`merge` + `switchMap`) cancela a requisição anterior se ela
ainda estiver pendente, evitando respostas fora de ordem.

**SSE em vez de WebSocket.** O dashboard só recebe; não precisa mandar nada pelo mesmo canal. SSE é
HTTP puro: passa pelo nginx e pelo rate limit, usa o cookie e a autenticação que já existem e
reconecta sozinho. WebSocket exigiria outro protocolo, autenticação própria no *handshake* e
reconexão manual.

**Pub/sub em duas camadas.** O `LISTEN/NOTIFY` leva o aviso a cada instância, e o `LiveEventHub`
de cada uma distribui o aviso para os navegadores conectados nela. Cada navegador continua com sua
fila limitada e independente.

**Mesma origem via proxy reverso.** O navegador fala só com o nginx, que encaminha `/api`. Não
existe CORS para configurar, e a API não fica exposta.

---

## Segurança

A funcionalidade central do produto, "faça requisições HTTP para URLs cadastradas pelo usuário", é
exatamente o vetor de **SSRF (Server-Side Request Forgery)**. Por isso a segurança está em camadas.

### 1. Allowlist de destinos (no cadastro e antes de cada verificação)

`UrlSafetyPolicy` aceita uma URL apenas se:

- for absoluta, com no máximo 2048 caracteres;
- usar `https` (ou `http`, se `AllowHttp` estiver ligado);
- **não** tiver usuário/senha embutidos (`https://user:pass@host` vazaria segredo para banco e logs);
- usar uma porta permitida (padrão: 443);
- tiver um host na allowlist (`api.exemplo.com`, `*.exemplo.com` ou `*`);
- se for um IP literal, ele precisa ser público.

### 2. Bloqueio de destinos privados na conexão (anti DNS rebinding)

Validar só o texto da URL não basta: `evil.com` pode resolver para um IP público no cadastro e
para `127.0.0.1` depois. O `SafeSocketConnector` é plugado no `SocketsHttpHandler.ConnectCallback`
e, **a cada conexão**:

1. resolve o DNS;
2. recusa se **qualquer** IP for loopback, privado (RFC 1918), link-local (inclui
   `169.254.169.254`, o serviço de metadados da nuvem), CGNAT, multicast, reservado, ULA IPv6, ou
   um IPv6 que embute IPv4 (mapeado, NAT64, 6to4, Teredo);
3. conecta exatamente nos IPs que validou, então não há janela entre checar e usar (TOCTOU).

Exceções são explícitas e por nome: `AllowedPrivateHosts` (no Compose, só `demo-app`).

### 3. O cliente HTTP é "blindado"

| Configuração | Motivo |
|---|---|
| `AllowAutoRedirect = false` | um `302` poderia apontar para a rede interna |
| `UseProxy = false` | com proxy, o IP validado seria o do proxy, não o do destino |
| `HttpCompletionOption.ResponseHeadersRead` | o corpo nunca é baixado |
| `MaxResponseHeadersLength = 64 KB` | limita cabeçalhos gigantes |
| `ConnectTimeout` + timeout total por `CancellationToken` | alvo lento não segura o agendador |
| `PooledConnectionLifetime = 2 min` | conexões são recriadas e o DNS revalidado |
| `UseCookies = false` | verificações não carregam estado |

### 4. Limites

- **Rate limiting por IP** (janela fixa): 120 leituras/min, 20 escritas/min e 30/min na página
  de status pública. Acima disso, `429` com `Retry-After`.
- **X-Forwarded-For só é confiado vindo do nginx** (`ReverseProxy:KnownNetworks`), e o nginx
  sobrescreve o cabeçalho. Um cliente não consegue trocar de IP para fugir do limite.
- **Máximo de aplicações** (`MaxApplications`, padrão 100) e **intervalo mínimo** de 30 s: cada
  cadastro gera tráfego de saída, então o DeployGuard não vira ferramenta de flood.
- **Concorrência máxima** de verificações (`MaxConcurrentChecks`).
- **Página máxima** de 500 itens no histórico e janela máxima de 7 dias nas estatísticas.
- **Conexões ao vivo** limitadas (`Live:MaxConnections`, padrão 100), com fila de no máximo 100
  eventos por conexão e duração máxima de 5 minutos.
- **Corpo de requisição** limitado a 32 KB (Kestrel e nginx).
- **Retenção**: resultados com mais de 30 dias são apagados de hora em hora.

### 5. Segredos

- Nenhuma senha no repositório. A connection string vem de variável de ambiente
  (`ConnectionStrings__DeployGuard`) ou do `dotnet user-secrets` em desenvolvimento.
- No Compose, a senha vem do `.env` (ignorado pelo git). `${POSTGRES_PASSWORD:?...}` faz o Compose
  falhar se ela não estiver definida, então não existe senha padrão. O mesmo vale para a chave dos
  tokens (`AUTH_JWT_KEY`) e para o admin inicial.
- **A chave de assinatura dos tokens é o segredo mais sensível:** com ela, qualquer um forjaria um
  token de Admin. A API não sobe com chave ausente ou menor que 32 caracteres.
- A API não sobe sem connection string, e as opções de monitoramento são validadas na
  inicialização (`ValidateOnStart`).
- Erros seguem Problem Details (RFC 9457) sem stack trace. Mensagens de falha de verificação são
  fixas, não a mensagem bruta da exceção.
- Logs registram o host, não a URL completa (query strings podem carregar tokens).
- **URLs de webhook são segredos** (quem tem a URL de um webhook do Slack pode postar no canal).
  Vêm só de variável de ambiente, exigem `https` e nunca aparecem em logs nem em mensagens de erro.

### 6. Autenticação e autorização

- **Fechado por padrão.** Uma *fallback policy* exige login em todo endpoint. Só `/health`, o login
  e o logout são públicos. Um endpoint novo esquecido nasce protegido, não aberto.
- **Papéis (RBAC).** `Admin` cadastra, altera e remove aplicações e usuários. `Viewer` só lê. A
  regra vale na API (`403`); o dashboard só esconde os botões para não confundir.
- **Senhas com PBKDF2** (`PasswordHasher` do ASP.NET Core Identity): hash lento com *salt*
  aleatório. A senha nunca é gravada nem devolvida pela API. Política: 12 a 128 caracteres e sem o
  nome do e-mail (comprimento vale mais que regras de símbolo, como recomenda o NIST).
- **Token JWT em cookie `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/api`.** O JavaScript não
  lê o cookie (um XSS não rouba a sessão) e o navegador não o envia em requisições vindas de
  outros sites (CSRF). Assinatura HMAC-SHA256 com algoritmo fixo, emissor, audiência e validade
  conferidos.
- **Revogação imediata.** A cada requisição a API confere se o usuário do token ainda existe, tem o
  mesmo papel e o mesmo **carimbo de segurança** (`SecurityStamp`, que vai dentro do token). Remover
  alguém corta o acesso na hora. Trocar a senha gera um carimbo novo: as sessões abertas em outros
  navegadores caem, e a sessão de quem trocou recebe um cookie novo.
- **Contra força bruta e enumeração.** Duas camadas:
  - **Por IP:** login limitado a 5 tentativas por minuto (rate limit).
  - **Por conta:** depois de 5 senhas erradas seguidas, a conta fica bloqueada por 15 minutos,
    mesmo para a senha certa e mesmo vindo de outro IP (protege contra ataques distribuídos). O
    bloqueio é temporário de propósito: um bloqueio permanente deixaria qualquer um trancar a conta
    de outra pessoa errando a senha. O Admin pode desbloquear antes. O contador sobe num único
    `UPDATE` atômico, então tentativas simultâneas (ou em réplicas diferentes) não se perdem.
  - A resposta é **idêntica** para e-mail inexistente, senha errada e conta bloqueada, inclusive no
    tempo (a API calcula um hash mesmo quando o e-mail não existe). Quem ataca não descobre quais
    contas existem nem quais estão bloqueadas.
- **Troca de senha** exige a senha atual, segue a mesma política e não aceita repetir a senha
  atual. Tem o mesmo rate limit do login, para quem roubou uma sessão não testar senhas à vontade.
- **Auditoria.** Logins (certos e recusados), bloqueios, troca de senha, usuários, aplicações e
  chaves de API ficam registrados com quem, quando, o quê e o IP. O registro entra no **mesmo
  `SaveChanges`** da ação (ou os dois são gravados, ou nenhum), nunca contém senha, chave ou token,
  e a API não tem rota para editar nem apagar registros. Só Admins consultam.
- **Sem open redirect.** Depois do login, o dashboard só volta para caminhos internos.
- **Chaves de API para máquinas** (o CI registrando deploys). São 32 bytes aleatórios
  (`dg_...`), mostradas **uma única vez** e guardadas só como hash SHA-256. Valem apenas para
  registrar deploy: em qualquer outro endpoint a resposta é `401` (menor privilégio). Revogar é
  imediato, e a lista mostra o último uso de cada chave.
- **Links de deploy só `https`**, porque viram `<a href>` no dashboard: `javascript:` ou `data:`
  seriam XSS.

### 7. Outras defesas

- Containers rodando como usuário não-root, imagem da API "chiseled" (sem shell), sistema de
  arquivos somente leitura e `no-new-privileges`.
- Banco e API sem portas publicadas. Dashboard publicado só em `127.0.0.1`.
- Cabeçalhos `Content-Security-Policy`, `X-Frame-Options`, `X-Content-Type-Options` e
  `Referrer-Policy`.
- Angular escapa toda interpolação (`{{ }}`), o que protege contra XSS com nomes maliciosos. Há um
  teste que garante isso.
- CI com `permissions: contents: read`, auditoria de pacotes vulneráveis (NuGet e npm) e Dependabot.

---

## API REST

Base: `/api`. JSON com enums como texto. Erros no formato Problem Details. Em Development o
documento OpenAPI fica em `/openapi/v1.json`. Tudo exige login, exceto onde indicado; rotas
marcadas com **Admin** respondem `403` para Viewers.

| Método | Rota | Descrição |
|---|---|---|
| `POST` | `/auth/login` | Público. `{ email, password }`. Devolve o usuário e grava o cookie de sessão |
| `POST` | `/auth/logout` | Público. Apaga o cookie |
| `GET` | `/auth/me` | Usuário logado (`id`, `email`, `role`) |
| `GET` | `/users` | **Admin.** Lista usuários (nunca devolve o hash da senha) |
| `POST` | `/users` | **Admin.** Cria (`email`, `password`, `role`: `Admin` ou `Viewer`, padrão `Viewer`) |
| `DELETE` | `/users/{id}` | **Admin.** Remove (não é possível remover a si mesmo) |
| `POST` | `/users/{id}/unlock` | **Admin.** Desbloqueia uma conta bloqueada por senhas erradas |
| `POST` | `/auth/password` | Troca a própria senha (`currentPassword`, `newPassword`). Encerra as outras sessões |
| `GET` | `/audit` | **Admin.** Registro de auditoria, mais recente primeiro. Query: `limit` (até 200), `before` (cursor), `action` |
| `GET` | `/applications` | Lista aplicações com estado atual e disponibilidade em 24h |
| `GET` | `/applications/{id}` | Detalhe de uma aplicação |
| `POST` | `/applications` | **Admin.** Cadastra (`name`, `healthCheckUrl`, `checkIntervalSeconds?`, `isPublic?`) |
| `PUT` | `/applications/{id}` | **Admin.** Atualiza campos (`name`, `healthCheckUrl`, `checkIntervalSeconds`, `isEnabled`, `isPublic`) |
| `DELETE` | `/applications/{id}` | **Admin.** Remove a aplicação e o histórico |
| `GET` | `/applications/{id}/checks` | Histórico paginado. Query: `limit` (1 a 500), `cursor`, `from`, `to`, `status` |
| `GET` | `/applications/{id}/stats` | Disponibilidade, latência média, p95, máxima e erros por tipo. Query: `hours` (1 a 168) |
| `GET` | `/incidents` | Incidentes de todas as aplicações. Query: `status` (`All`, `Open`, `Resolved`), `limit` (até 200) |
| `GET` | `/applications/{id}/incidents` | Incidentes de uma aplicação. Cada incidente traz `suspectedDeployment` quando houve deploy até 30 min antes |
| `GET` | `/deployments` | Últimos deploys de todas as aplicações. Query: `limit` (até 200) |
| `GET` | `/applications/{id}/deployments` | Deploys de uma aplicação |
| `POST` | `/applications/{id}/deployments` | **Admin ou chave de API.** Registra (`version`, `commitSha?`, `description?`, `url?` https, `deployedAt?`) |
| `GET` | `/api-keys` | **Admin.** Lista as chaves (nome, prefixo, último uso; nunca a chave) |
| `POST` | `/api-keys` | **Admin.** Cria (`name`). A resposta traz a chave completa, só desta vez |
| `DELETE` | `/api-keys/{id}` | **Admin.** Revoga |
| `GET` | `/events` | Canal ao vivo (Server-Sent Events): `ready`, `check`, `incident-opened`, `incident-resolved`, `deployment` e `ping`. `503` acima do limite de conexões |
| `GET` | `/public/status` | Público. Página de status: situação geral e, para cada aplicação publicada, nome, situação e disponibilidade por dia. Cache de 30 s |
| `GET` | `/health` | Público (fora de `/api`). Liveness da própria API |

Exemplos:

```bash
# -c/-b guardam e reenviam o cookie de sessão, como um navegador
curl -c cookies.txt -X POST http://localhost:8080/api/auth/login \
  -H 'Content-Type: application/json' -d '{"email":"admin@example.com","password":"..."}'

curl -b cookies.txt -X POST http://localhost:8080/api/applications \
  -H 'Content-Type: application/json' \
  -d '{"name":"Demo","healthCheckUrl":"http://demo-app:8080/health","checkIntervalSeconds":30}'

curl -b cookies.txt "http://localhost:8080/api/applications/{id}/checks?limit=20"
# → { "items": [...], "nextCursor": "MTM4..." }   passe ?cursor=... para a próxima página

curl -b cookies.txt "http://localhost:8080/api/applications/{id}/stats?hours=24"

# Canal ao vivo: -N desliga o buffer do curl para ver cada evento chegar
curl -N -b cookies.txt http://localhost:8080/api/events
# event: ready
#
# event: check
# data: {"type":"CheckCompleted","applicationId":"...","status":"Up","latencyMs":42,...}

# No pipeline de CI, com uma chave de API em vez do cookie:
curl -X POST http://localhost:8080/api/applications/{id}/deployments \
  -H "Authorization: Bearer dg_..." -H 'Content-Type: application/json' \
  -d '{"version":"v1.4.2","commitSha":"a1b2c3d"}'
```

Um workflow completo do GitHub Actions que avisa cada deploy está em
[`docs/exemplos/avisar-deploy.yml`](docs/exemplos/avisar-deploy.yml).

Resposta de validação (`400`):

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": { "healthCheckUrl": ["Destinos em redes privadas ou locais não são permitidos."] }
}
```

---

## Configuração

Tudo pode vir do `appsettings.json` ou de variáveis de ambiente (`:` vira `__`, ex.:
`Monitoring__AllowedHosts__0=*.exemplo.com`).

| Chave | Padrão | Descrição |
|---|---|---|
| `ConnectionStrings:DeployGuard` | (obrigatória) | Conexão com o PostgreSQL |
| `Database:MigrateOnStartup` | `false` (`true` em Development) | Aplica migrations ao iniciar |
| `Monitoring:AllowedHosts` | `[]` (`["*"]` em Development) | Allowlist de hosts |
| `Monitoring:AllowedPrivateHosts` | `[]` | Hosts autorizados a resolver para IP privado |
| `Monitoring:AllowedPorts` | `[443]` | Portas permitidas |
| `Monitoring:AllowHttp` | `false` | Aceitar `http://` |
| `Monitoring:RequestTimeoutSeconds` | `5` | Timeout total de uma verificação |
| `Monitoring:ConnectTimeoutSeconds` | `3` | Timeout de conexão TCP |
| `Monitoring:MinIntervalSeconds` / `MaxIntervalSeconds` / `DefaultIntervalSeconds` | `30` / `3600` / `60` | Limites do intervalo |
| `Monitoring:MaxConcurrentChecks` | `10` | Verificações simultâneas |
| `Monitoring:MaxApplications` | `100` | Máximo de aplicações cadastradas |
| `Monitoring:DegradedLatencyThresholdMs` | `1000` | Acima disso, 2xx vira `Degraded` |
| `Monitoring:ResultRetentionDays` | `30` | Retenção do histórico |
| `Monitoring:SchedulerTickSeconds` | `5` | Frequência do agendador |
| `RateLimiting:ReadRequestsPerMinute` / `WriteRequestsPerMinute` / `LoginRequestsPerMinute` / `PublicRequestsPerMinute` | `120` / `20` / `5` / `30` | Limites por IP |
| `StatusPage:Title` | `Status dos serviços` | Título da página pública |
| `StatusPage:HistoryDays` | `90` | Dias de histórico (limitado pela retenção) |
| `StatusPage:CacheSeconds` | `30` | Cache da resposta pública (0 desliga) |
| `Auth:JwtSigningKey` | (obrigatória, segredo) | Chave HMAC dos tokens, 32+ caracteres |
| `Auth:TokenLifetimeMinutes` | `480` | Validade da sessão |
| `Auth:CookieSecure` | `true` | Cookie só em HTTPS (navegadores tratam `localhost` como seguro) |
| `Auth:BootstrapAdminEmail` / `BootstrapAdminPassword` | (vazio, segredo) | Primeiro admin, criado só se não houver nenhum usuário |
| `Auth:Issuer` / `Audience` | `deployguard` / `deployguard-dashboard` | Conferidos em todo token |
| `Auth:MaxFailedLogins` | `5` | Senhas erradas seguidas antes de bloquear a conta |
| `Auth:LockoutMinutes` | `15` | Duração do bloqueio |
| `ReverseProxy:KnownNetworks` | `[]` | Redes (CIDR) cujo `X-Forwarded-For` é confiável |
| `Alerting:FailureThreshold` | `2` | Falhas consecutivas para abrir um incidente |
| `Alerting:Webhooks` | `[]` | Lista de `{ Name, Url, Format }`. `Format`: `Slack`, `Discord` ou `Generic`. URL vazia = canal desligado |
| `Alerting:MaxDeliveryAttempts` | `6` | Tentativas de entrega antes de desistir |
| `Alerting:DispatchIntervalSeconds` | `10` | Frequência do dispatcher de alertas |
| `Alerting:WebhookTimeoutSeconds` | `10` | Timeout de cada chamada ao webhook |
| `Alerting:DashboardBaseUrl` | (vazio) | Se definido, a mensagem inclui um link para a aplicação |
| `Deployments:CorrelationWindowMinutes` | `30` | Um incidente que começa até N minutos depois de um deploy aponta esse deploy como suspeito |
| `Live:MaxConnections` | `100` | Conexões ao vivo simultâneas por instância |
| `Live:HeartbeatSeconds` | `15` | Intervalo do `ping` quando não há eventos |
| `Live:MaxStreamMinutes` | `5` | Duração máxima de uma conexão (o navegador reconecta e passa pelo login de novo) |
| `Live:BufferPerConnection` | `100` | Eventos guardados para um cliente lento antes de descartar os mais antigos |

---

## Testes e CI

```bash
# Backend: usa Testcontainers (precisa de Docker) ou um PostgreSQL seu:
cd backend
dotnet test
DEPLOYGUARD_TEST_CONNECTION="Host=localhost;Username=postgres;Password=..." dotnet test

# Frontend
cd frontend
npm run test:ci
```

**Backend (168 testes)**

- Unidade: `IpAddressGuard` (faixas privadas, IPv6, IPv4 mapeado), `UrlSafetyPolicy` (esquemas,
  credenciais, portas, curingas, IP literal), `SafeSocketConnector` (DNS rebinding, inclusive com um
  servidor TCP real provando que nenhum byte é enviado), `HealthChecker` (Up, Degraded, Down,
  timeout, classificação de erros) e paginação.
- Integração: a API inteira sobe em memória com `WebApplicationFactory` contra um PostgreSQL real.
  Cobre CRUD, validação, agendador gravando resultados, paginação por cursor, estatísticas, rate
  limiting (429), limite de aplicações e cabeçalhos de segurança.
- Alertas: todas as transições do `IncidentTracker`, backoff, formato das mensagens e do JSON de
  cada canal, ciclo de vida completo do incidente com o dispatcher, retentativas até desistir e o
  índice que impede dois incidentes abertos.
- Tempo real: o `LiveEventHub` (entrega, limite de conexões, descarte dos eventos antigos), o fluxo
  SSE com relógio falso (`ready`, evento, `ping`, fim por tempo máximo) e, na integração, um
  cliente lendo `/api/events` de verdade enquanto o agendador roda, além de `401` e `503`.
- Várias instâncias: dois hosts da API no mesmo banco. As duas dividem 30 aplicações sem verificar
  nenhuma em dobro (o teste falha se a reserva com `SKIP LOCKED` for trocada por um `SELECT`
  simples), uma reserva some até o prazo vencer, dois dispatchers entregam cada alerta uma vez só,
  um navegador conectado na instância B recebe o aviso de uma verificação feita pela A, um `NOTIFY`
  de transação desfeita nunca é entregue, e duas subidas simultâneas aplicam as migrations uma vez.
- Conta e auditoria: bloqueio depois de 5 erros e desbloqueio pelo Admin, fim automático do
  bloqueio, 5 tentativas simultâneas todas contadas, respostas idênticas para e-mail inexistente,
  senha errada e conta bloqueada, troca de senha derrubando a outra sessão e mantendo a atual, e a
  auditoria registrando quem fez o quê sem nunca guardar a senha digitada.

**Frontend (60 testes)**: serviço HTTP com `HttpTestingController`, dashboard (cards, resumo, estado
vazio, erro de rede, escape de HTML, banner de incidentes, avisos ao vivo), canal ao vivo com um
`EventSource` falso (reconexão e conexão compartilhada), formulário (validação local e erros vindos da API), badge,
gráfico, pipe de tempo relativo, troca de senha (confirmação e erros da API), desbloqueio na tela de
usuários e auditoria (rótulos, filtro e paginação).

**GitHub Actions** (`.github/workflows/ci.yml`) roda em todo push e PR: build e testes do backend com
PostgreSQL como service container, auditoria de pacotes, formatação, testes e build do Angular,
`npm audit`, e build das imagens Docker.

---

## Estrutura de pastas

```
backend/
  src/DeployGuard.Api/
    Domain/           entidades e enums (MonitoredApplication, HealthCheckResult, CheckStatus)
    Data/             DbContext, migrations e migração segura com várias instâncias
    Applications/     endpoints de cadastro, validação, DTOs
    Monitoring/       política de URL, guarda de IP, conector seguro, checker, agendador
    History/          endpoints de histórico e estatísticas, cursor de paginação
    Incidents/        endpoints de incidentes
    Alerting/         máquina de estados do incidente, outbox dispatcher, envio por webhook
    Auth/             login, JWT, política de senha, usuários, chaves de API
    Deployments/      registro de deploys, validação e correlação com incidentes
    Live/             canal ao vivo: LISTEN/NOTIFY entre instâncias, pub/sub local e endpoint SSE
    StatusPage/       página de status pública: rota anônima, DTO mínimo e cache
    Infrastructure/   rate limiting e cabeçalhos de segurança
    Program.cs        composição da aplicação (DI e pipeline HTTP)
  tests/DeployGuard.Tests/
    Unit/  Integration/  TestDoubles/
frontend/
  src/app/
    core/             modelos, cliente da API, canal ao vivo, Problem Details, sessão, guards, interceptor
    login/            tela de login
    users/            gestão de usuários e desbloqueio de contas (só Admin)
    account/          "Minha conta": troca de senha
    audit/            registro de auditoria (só Admin)
    status/           página de status pública (sem login)
    api-keys/         chaves de API para o CI (só Admin)
    dashboard/        lista de aplicações e formulário de cadastro
    application-detail/  estatísticas, gráfico de latência e histórico
    shared/           badge de status, pipe de tempo relativo
  nginx.conf          servidor estático + proxy /api + cabeçalhos de segurança
docker-compose.yml
.github/workflows/ci.yml
```

---

## Limitações conhecidas e próximos passos

- **Login corporativo (SSO/OIDC)** com Entra ID, Google ou Keycloak, e MFA.
- **Recuperação de senha por e-mail** ("esqueci minha senha"). Hoje um Admin remove e recria o
  usuário.
- **Retenção da auditoria**: os registros crescem sem limite. Em produção, guardar por um prazo
  definido (ex.: 1 ano) e exportar para um sistema de logs (SIEM).
- **Limites por instância**: o rate limit e o limite de conexões ao vivo ficam na memória de cada
  réplica, então com duas réplicas o teto efetivo dobra. Um limite global exigiria um contador
  compartilhado (Redis, por exemplo).
- **Limite de conexões ao vivo por usuário**: hoje o limite é por instância, então um usuário logado
  com muitas abas poderia ocupar as vagas (os outros continuariam com a atualização de 1 minuto).
- **nginx resolve o endereço das réplicas ao iniciar.** Se uma réplica for recriada com outro IP, é
  preciso reiniciar o `web`. Em produção, um orquestrador (Kubernetes, ECS) ou um `resolver` no
  nginx cuida disso.
- **Limpeza de resultados antigos** roda em todas as réplicas. É inofensivo (o `DELETE` é idempotente),
  mas trabalho repetido.
- **Página de status com aviso manual**: hoje ela é 100% automática. Um próximo passo é o Admin
  escrever avisos ("manutenção programada às 22h") e mostrar o histórico de incidentes encerrados.
- **Mais canais de alerta** (e-mail, SMS) e regras por aplicação (quem avisar, horário de plantão).
- **Particionamento** da tabela de resultados por data, se o volume crescer.
- **Validação do corpo** da resposta (ex.: exigir `"status":"ok"`) com limite de bytes lidos.
- Visão futura do produto: OpenTelemetry, AWS CloudWatch, correlação com deploys, investigação
  assistida e integração com Azure.
