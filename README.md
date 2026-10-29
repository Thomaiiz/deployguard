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

| Camada | Tecnologia |
|---|---|
| API e agendador | ASP.NET Core (.NET 10 LTS), minimal APIs, EF Core 10 |
| Banco | PostgreSQL 17 (Npgsql) |
| Dashboard | Angular 22 (standalone components, signals, zoneless) |
| Testes | xUnit, WebApplicationFactory, Testcontainers, Vitest |
| Infra | Docker Compose, nginx, GitHub Actions, Dependabot |

> Fora do escopo desta etapa (visão futura): AWS CloudWatch, OpenTelemetry, correlação de
> deploys, investigação assistida por IA e integração com Azure.

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
        subgraph api[api: ASP.NET Core]
            endpoints[Endpoints REST<br/>+ rate limiting]
            scheduler[HealthCheckScheduler<br/>BackgroundService]
            checker[HealthChecker<br/>+ SafeSocketConnector]
            dispatcher[AlertDispatcher<br/>BackgroundService]
        end
        db[(PostgreSQL)]
        demo[demo-app]
    end

    nginx -->|/api| endpoints
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

**Agendador dentro da API (`BackgroundService`).** Um único processo para subir e operar. O custo
é que, com várias instâncias da API, cada uma verificaria as mesmas aplicações. Quando houver escala
horizontal, o caminho é separar um worker ou usar `SELECT ... FOR UPDATE SKIP LOCKED` para
distribuir o trabalho.

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

**Angular com signals e polling.** O estado de cada tela fica em `signal`s e `computed`s. O polling
usa RxJS (`timer` + `switchMap`), que cancela a requisição anterior se ela ainda estiver pendente.
Server-Sent Events ou WebSockets seriam o próximo passo se a latência de 15 s incomodar.

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

- **Rate limiting por IP** (janela fixa): 120 leituras/min e 20 escritas/min. Acima disso, `429`
  com `Retry-After`.
- **X-Forwarded-For só é confiado vindo do nginx** (`ReverseProxy:KnownNetworks`), e o nginx
  sobrescreve o cabeçalho. Um cliente não consegue trocar de IP para fugir do limite.
- **Máximo de aplicações** (`MaxApplications`, padrão 100) e **intervalo mínimo** de 30 s: cada
  cadastro gera tráfego de saída, então o DeployGuard não vira ferramenta de flood.
- **Concorrência máxima** de verificações (`MaxConcurrentChecks`).
- **Página máxima** de 500 itens no histórico e janela máxima de 7 dias nas estatísticas.
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
- **Revogação imediata.** A cada requisição a API confere se o usuário do token ainda existe e tem
  o mesmo papel. Remover alguém corta o acesso na hora, sem esperar o token expirar.
- **Contra força bruta e enumeração.** Login limitado a 5 tentativas por minuto por IP. A mensagem
  de erro é a mesma para e-mail inexistente e senha errada, e o tempo de resposta também (a API
  calcula um hash mesmo quando o e-mail não existe).
- **Sem open redirect.** Depois do login, o dashboard só volta para caminhos internos.

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
| `GET` | `/applications` | Lista aplicações com estado atual e disponibilidade em 24h |
| `GET` | `/applications/{id}` | Detalhe de uma aplicação |
| `POST` | `/applications` | **Admin.** Cadastra (`name`, `healthCheckUrl`, `checkIntervalSeconds?`) |
| `PUT` | `/applications/{id}` | **Admin.** Atualiza campos (`name`, `healthCheckUrl`, `checkIntervalSeconds`, `isEnabled`) |
| `DELETE` | `/applications/{id}` | **Admin.** Remove a aplicação e o histórico |
| `GET` | `/applications/{id}/checks` | Histórico paginado. Query: `limit` (1 a 500), `cursor`, `from`, `to`, `status` |
| `GET` | `/applications/{id}/stats` | Disponibilidade, latência média, p95, máxima e erros por tipo. Query: `hours` (1 a 168) |
| `GET` | `/incidents` | Incidentes de todas as aplicações. Query: `status` (`All`, `Open`, `Resolved`), `limit` (até 200) |
| `GET` | `/applications/{id}/incidents` | Incidentes de uma aplicação |
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
```

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
| `RateLimiting:ReadRequestsPerMinute` / `WriteRequestsPerMinute` / `LoginRequestsPerMinute` | `120` / `20` / `5` | Limites por IP |
| `Auth:JwtSigningKey` | (obrigatória, segredo) | Chave HMAC dos tokens, 32+ caracteres |
| `Auth:TokenLifetimeMinutes` | `480` | Validade da sessão |
| `Auth:CookieSecure` | `true` | Cookie só em HTTPS (navegadores tratam `localhost` como seguro) |
| `Auth:BootstrapAdminEmail` / `BootstrapAdminPassword` | (vazio, segredo) | Primeiro admin, criado só se não houver nenhum usuário |
| `Auth:Issuer` / `Audience` | `deployguard` / `deployguard-dashboard` | Conferidos em todo token |
| `ReverseProxy:KnownNetworks` | `[]` | Redes (CIDR) cujo `X-Forwarded-For` é confiável |
| `Alerting:FailureThreshold` | `2` | Falhas consecutivas para abrir um incidente |
| `Alerting:Webhooks` | `[]` | Lista de `{ Name, Url, Format }`. `Format`: `Slack`, `Discord` ou `Generic`. URL vazia = canal desligado |
| `Alerting:MaxDeliveryAttempts` | `6` | Tentativas de entrega antes de desistir |
| `Alerting:DispatchIntervalSeconds` | `10` | Frequência do dispatcher de alertas |
| `Alerting:WebhookTimeoutSeconds` | `10` | Timeout de cada chamada ao webhook |
| `Alerting:DashboardBaseUrl` | (vazio) | Se definido, a mensagem inclui um link para a aplicação |

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

**Backend (95 testes)**

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

**Frontend (24 testes)**: serviço HTTP com `HttpTestingController`, dashboard (cards, resumo, estado
vazio, erro de rede, escape de HTML, banner de incidentes), formulário (validação local e erros vindos da API), badge,
gráfico e pipe de tempo relativo.

**GitHub Actions** (`.github/workflows/ci.yml`) roda em todo push e PR: build e testes do backend com
PostgreSQL como service container, auditoria de pacotes, formatação, testes e build do Angular,
`npm audit`, e build das imagens Docker.

---

## Estrutura de pastas

```
backend/
  src/DeployGuard.Api/
    Domain/           entidades e enums (MonitoredApplication, HealthCheckResult, CheckStatus)
    Data/             DbContext e migrations do EF Core
    Applications/     endpoints de cadastro, validação, DTOs
    Monitoring/       política de URL, guarda de IP, conector seguro, checker, agendador
    History/          endpoints de histórico e estatísticas, cursor de paginação
    Incidents/        endpoints de incidentes
    Alerting/         máquina de estados do incidente, outbox dispatcher, envio por webhook
    Auth/             login, emissão e validação do JWT, política de senha, usuários
    Infrastructure/   rate limiting e cabeçalhos de segurança
    Program.cs        composição da aplicação (DI e pipeline HTTP)
  tests/DeployGuard.Tests/
    Unit/  Integration/  TestDoubles/
frontend/
  src/app/
    core/             modelos, cliente da API, Problem Details, sessão (AuthService), guards, interceptor
    login/            tela de login
    users/            gestão de usuários (só Admin)
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
- **Troca de senha** pelo próprio usuário e bloqueio de conta após muitas falhas (hoje só há o
  rate limit por IP).
- **Várias instâncias**: distribuir o agendamento (`FOR UPDATE SKIP LOCKED` ou worker separado).
- **Mais canais de alerta** (e-mail, SMS) e regras por aplicação (quem avisar, horário de plantão).
- **Tempo real** no dashboard com Server-Sent Events.
- **Particionamento** da tabela de resultados por data, se o volume crescer.
- **Validação do corpo** da resposta (ex.: exigir `"status":"ok"`) com limite de bytes lidos.
- Visão futura do produto: OpenTelemetry, AWS CloudWatch, correlação com deploys, investigação
  assistida e integração com Azure.
