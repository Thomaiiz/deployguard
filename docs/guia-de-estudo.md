# Guia de estudo do DeployGuard

Este guia acompanha a primeira etapa do projeto. Para cada etapa ele explica o que foi feito, os
comandos usados e os conceitos por trás, e termina com perguntas que costumam aparecer em
entrevistas técnicas, com respostas curtas baseadas neste código.

---

## Etapa 1: estrutura da solução

### Comandos

| Comando | O que faz |
|---|---|
| `dotnet new sln -n DeployGuard` | Cria a *solution*, o arquivo que agrupa projetos .NET (a API e os testes). |
| `dotnet new web -n DeployGuard.Api` | Cria um projeto ASP.NET Core vazio (template de minimal API). |
| `dotnet new xunit -n DeployGuard.Tests` | Cria um projeto de testes com xUnit. |
| `dotnet sln add <projeto>` | Inclui o projeto na solution. |
| `dotnet add <testes> reference <api>` | Faz o projeto de testes enxergar as classes da API. |
| `dotnet add package <nome>` | Adiciona um pacote NuGet (ex.: `Npgsql.EntityFrameworkCore.PostgreSQL`). |
| `dotnet new tool-manifest` + `dotnet tool install dotnet-ef` | Instala a ferramenta de migrations *no repositório* (`dotnet-tools.json`), então todo mundo usa a mesma versão com `dotnet tool restore`. |
| `dotnet new gitignore` | Gera um `.gitignore` padrão para .NET (ignora `bin/`, `obj/` etc.). |

### Conceitos

- **Entidade vs. DTO.** `MonitoredApplication` é a entidade (como o dado vive no banco).
  `ApplicationResponse` é o DTO (o contrato que a API expõe). Separar os dois permite mudar o banco
  sem quebrar clientes e evita expor campos internos, como `NextCheckAt`.
- **EF Core e `DbContext`.** O EF Core é o ORM: traduz LINQ em SQL. O `DbContext` representa uma
  "unidade de trabalho" com o banco. Em `OnModelCreating` configuramos tabela, tamanhos de coluna,
  índices e relacionamentos (*Fluent API*).
- **Enums gravados como texto** (`HasConversion<string>()`). No banco fica `"Down"` e não `2`:
  legível em consultas manuais e seguro se alguém reordenar o enum.
- **Índices compostos.** `(ApplicationId, CheckedAt DESC)` atende exatamente a consulta "histórico
  de uma aplicação, mais recente primeiro". `(IsEnabled, NextCheckAt)` atende o agendador.
- **`Guid.CreateVersion7`.** UUID v7 começa com o timestamp, então IDs novos são "quase
  ordenados". Isso fragmenta menos o índice da chave primária do que um UUID v4 aleatório.

---

## Etapa 2: cadastro de aplicações

### Comandos

| Comando | O que faz |
|---|---|
| `dotnet user-secrets set "ConnectionStrings:DeployGuard" "..."` | Guarda a connection string no perfil do seu usuário (fora da pasta do projeto). Só vale em Development. |
| `dotnet ef migrations add InitialCreate -o Data/Migrations` | Compara o modelo do `DbContext` com o último snapshot e gera o código C# que cria/altera as tabelas. |
| `dotnet run` | Compila e sobe a API. Em Development ela aplica as migrations sozinha. |
| `curl -X POST ... -d '{...}'` | Faz uma requisição HTTP pela linha de comando, útil para testar a API sem frontend. |

### Conceitos

- **Minimal APIs.** Endpoints declarados com `MapGet`, `MapPost` etc. Os parâmetros do método são
  preenchidos automaticamente: rota (`Guid id`), corpo JSON (`CreateApplicationRequest`), serviços
  da injeção de dependência (`DeployGuardDbContext`) e o `CancellationToken` da requisição.
- **`MapGroup`.** Agrupa rotas com um prefixo e políticas em comum (ex.: rate limiting em todo
  `/api`).
- **Injeção de dependência (DI).** Classes recebem o que precisam pelo construtor e o container cria
  tudo. *Lifetimes*: `Singleton` (uma instância para a aplicação inteira), `Scoped` (uma por
  requisição, como o `DbContext`) e `Transient` (uma a cada pedido).
- **Validação e Problem Details.** Erros de entrada voltam como `400` no formato RFC 9457, com um
  dicionário campo → mensagens. O frontend usa isso para mostrar o erro embaixo do campo certo.
- **Códigos HTTP usados.** `201 Created` (com cabeçalho `Location`), `204 No Content` no delete,
  `404`, `400` para validação, `409 Conflict` para limite de aplicações e `429` para rate limiting.
- **`ExecuteDeleteAsync`.** Gera um `DELETE ... WHERE` direto, sem carregar a entidade antes. O
  histórico vai junto pela *foreign key* com `ON DELETE CASCADE`.

---

## Etapa 3: verificações HTTP seguras

Esta é a parte mais importante para explicar em entrevista.

### O problema: SSRF

O DeployGuard faz requisições para URLs que o usuário cadastra. Sem proteção, alguém poderia
cadastrar:

- `http://169.254.169.254/latest/meta-data/`: o serviço de metadados da AWS, que pode devolver
  credenciais temporárias da máquina;
- `http://localhost:5432` ou `http://10.0.0.5/admin`: serviços internos que nunca deveriam ser
  acessados de fora.

O servidor viraria uma "ponte" para dentro da rede. Isso se chama **SSRF (Server-Side Request
Forgery)** e está no OWASP Top 10.

### As camadas de defesa

1. **`UrlSafetyPolicy`**: valida o texto da URL (esquema, credenciais, porta, allowlist de hosts, IP
   literal). Roda no cadastro e de novo antes de cada verificação.
2. **`IpAddressGuard`**: sabe quais faixas de IP não são públicas, usando `System.Net.IPNetwork`
   (notação CIDR, ex.: `10.0.0.0/8`). Trata o caso traiçoeiro de IPv6 que carrega um IPv4 dentro
   (`::ffff:10.0.0.1`).
3. **`SafeSocketConnector`**: plugado no `SocketsHttpHandler.ConnectCallback`, é chamado toda vez
   que o `HttpClient` precisa abrir uma conexão TCP. Resolve o DNS, valida **todos** os IPs e conecta
   neles.

### DNS rebinding e TOCTOU

Por que não basta validar no cadastro? Porque o atacante controla o DNS do domínio dele. No
cadastro `evil.com` responde `93.184.216.34` (público, passa). Minutos depois passa a responder
`127.0.0.1`. Se a validação e a conexão fizessem resoluções de DNS separadas, haveria uma janela
entre **checar** e **usar** (*time-of-check to time-of-use*, TOCTOU). Como o `SafeSocketConnector`
valida os IPs e conecta exatamente neles, essa janela não existe.

### Configuração do `SocketsHttpHandler`

- `AllowAutoRedirect = false`: um destino público poderia responder `302 Location: http://10.0.0.1`.
- `UseProxy = false`: se houvesse proxy, a conexão iria para o IP do proxy e a validação seria
  inútil.
- `HttpCompletionOption.ResponseHeadersRead`: o método retorna quando chegam os cabeçalhos; o corpo
  nunca é baixado.
- `PooledConnectionLifetime`: conexões são reaproveitadas (*connection pooling*), mas recriadas a
  cada 2 minutos, o que revalida o DNS.

### Timeouts e cancelamento

- `ConnectTimeout` limita só a conexão TCP.
- O timeout total é um `CancellationTokenSource` com `CancelAfter`, *linkado* ao token de
  desligamento da aplicação. Assim dá para distinguir "o alvo demorou" (vira `Timeout`) de "a
  aplicação está desligando" (propaga o cancelamento).

### `IHttpClientFactory`

Criar `new HttpClient()` a cada requisição esgota sockets. Um `HttpClient` estático eterno ignora
mudanças de DNS. O `IHttpClientFactory` resolve as duas coisas gerenciando o ciclo de vida dos
handlers, e é onde registramos o handler seguro com nome (`"health-checks"`).

### O agendador (`BackgroundService`)

- `BackgroundService` é um serviço que roda em segundo plano dentro do processo da API, iniciado e
  parado junto com ela.
- `PeriodicTimer` dispara a cada N segundos e não acumula ticks se uma rodada atrasar.
- O agendador é *singleton*, mas o `DbContext` é *scoped*. Por isso ele cria um escopo
  (`IServiceScopeFactory.CreateAsyncScope`) por rodada.
- `Parallel.ForEachAsync` com `MaxDegreeOfParallelism` executa várias verificações ao mesmo tempo
  com um teto. O `DbContext` **não** é usado dentro do laço paralelo, porque não é thread-safe.
- Um `try/catch` por rodada impede que uma falha (ex.: banco fora do ar) mate o serviço.

---

## Etapa 4: histórico e estatísticas

### Conceitos

- **Paginação keyset (por cursor).** Com `OFFSET 10000 LIMIT 50` o banco ainda precisa percorrer
  10.000 linhas, e se chegam dados novos entre uma página e outra, itens se repetem. Keyset diz
  "me dê os 50 seguintes depois de `(CheckedAt, Id)`", e o índice vai direto ao ponto. O `Id` entra
  como desempate quando dois resultados têm o mesmo horário.
- **Cursor opaco.** O cliente recebe uma string em Base64 e devolve como está. Assim dá para mudar
  o formato interno sem quebrar clientes. O cursor é validado (tamanho e conteúdo) porque vem do
  cliente.
- **Pedir `limit + 1`.** Buscamos um item a mais só para saber se existe próxima página, sem fazer
  um `COUNT(*)`.
- **Percentil p95.** "95% das verificações responderam em até X ms". É mais honesto que a média, que
  esconde picos. Calculado pelo método *nearest rank* sobre a lista ordenada. A janela é limitada a
  7 dias, então trazer as latências para a memória é barato.
- **Disponibilidade.** `(Up + Degraded) / total`. `Degraded` respondeu, só que devagar, então conta
  como disponível.

---

## Etapa 5: dashboard Angular

### Comandos

| Comando | O que faz |
|---|---|
| `npx @angular/cli new frontend --zoneless --style=scss --ssr=false` | Cria o projeto Angular sem zone.js, com SCSS e sem renderização no servidor. |
| `npm start` (`ng serve`) | Servidor de desenvolvimento com recarga automática. Usa `proxy.conf.json` para encaminhar `/api` para a API. |
| `npm run build` (`ng build`) | Gera os arquivos estáticos otimizados em `dist/`. |
| `npm run test:ci` (`ng test --watch=false`) | Roda os testes com Vitest uma vez (sem modo observação). |
| `npx prettier --write` | Formata o código num padrão único. |

### Conceitos

- **Standalone components.** Cada componente declara o que usa em `imports`, sem `NgModule`.
- **Signals.** `signal()` guarda um valor reativo, `computed()` deriva valores (ex.: o resumo
  "3 operacionais, 1 fora do ar") e o template se atualiza sozinho quando eles mudam.
- **Zoneless.** Sem zone.js, o Angular não "adivinha" quando algo mudou; ele reage aos signals.
  Menos mágica e melhor desempenho.
- **`OnPush`.** O componente só é re-renderizado quando seus inputs ou signals mudam.
- **RxJS no polling.** `timer(0, 15000)` emite agora e a cada 15 s. `switchMap` troca para a nova
  requisição e cancela a anterior se ainda estiver pendente. `takeUntilDestroyed()` encerra tudo
  quando o usuário sai da tela, evitando *memory leaks*.
- **Reactive Forms.** O formulário é um objeto (`FormGroup`) com validadores. A validação no
  cliente é só conveniência: a regra real está na API, porque o cliente pode ser burlado.
- **`withComponentInputBinding`.** O parâmetro `:id` da rota chega direto como `input()` do
  componente.
- **XSS.** `{{ valor }}` sempre escapa HTML. Um nome como `<img onerror=...>` aparece como texto.

---

## Etapa 6: testes

### Comandos

| Comando | O que faz |
|---|---|
| `dotnet test` | Compila e roda todos os testes xUnit. |
| `DEPLOYGUARD_TEST_CONNECTION=... dotnet test` | Usa um PostgreSQL existente em vez de subir um container. |

### Conceitos

- **Pirâmide de testes.** Muitos testes de unidade (rápidos, isolados) e alguns de integração
  (mais lentos, mais realistas).
- **Test doubles.** `StubHttpMessageHandler` responde sem rede. `FakeHealthChecker` substitui o
  checker nos testes de integração. `FakeTimeProvider` permite "avançar o relógio" e testar latência
  sem `sleep`.
- **`[Theory]` + `[InlineData]`.** Um teste parametrizado: a mesma verificação para dezenas de IPs e
  URLs.
- **`WebApplicationFactory<Program>`.** Sobe a API inteira em memória, com o pipeline real (rate
  limiting, validação, serialização), e dá um `HttpClient` para chamá-la.
- **Banco real nos testes de integração.** Banco em memória ou SQLite se comportam diferente do
  PostgreSQL (tipos, *timezone*, traduções de SQL). **Testcontainers** sobe um PostgreSQL
  descartável em Docker. Cada classe de teste usa um banco com nome aleatório.
- **Teste de segurança que prova o bloqueio.** Em `SafeSocketConnectorTests`, um `TcpListener` real
  fica escutando. O teste faz a requisição e confere que nenhuma conexão chegou ao servidor.

---

## Etapa 7: Docker, Compose e CI

### Comandos

| Comando | O que faz |
|---|---|
| `cp .env.example .env` | Cria o arquivo de segredos local, que o git ignora. |
| `docker compose up --build` | Constrói as imagens e sobe todos os serviços. |
| `docker compose ps` / `logs api` | Mostra o estado dos containers / os logs de um serviço. |
| `docker compose down -v` | Derruba tudo e apaga os volumes (zera o banco). |

### Conceitos

- **Multi-stage build.** A primeira etapa tem o SDK completo para compilar. A segunda só tem o
  runtime e o resultado. A imagem final é menor e não carrega compilador.
- **Cache de camadas.** Copiar o `.csproj` e rodar `restore` antes de copiar o código faz o Docker
  reaproveitar a camada de dependências quando só o código muda.
- **Imagem chiseled.** Ubuntu mínimo sem shell nem gerenciador de pacotes, rodando como não-root.
- **Proxy reverso.** O nginx serve o Angular e encaminha `/api` para a API. Para o navegador é uma
  origem só, então não há CORS.
- **Redes do Compose.** Os serviços se acham pelo nome (`db`, `api`, `demo-app`). Só o `web` publica
  porta, e só em `127.0.0.1`.
- **`healthcheck` + `depends_on: condition: service_healthy`.** A API só sobe depois que o
  PostgreSQL aceita conexões.
- **GitHub Actions.** Um *workflow* com *jobs* que rodam em máquinas limpas. *Service containers*
  sobem um PostgreSQL para os testes. `permissions: contents: read` limita o que o token pode fazer.
- **Dependabot.** Abre PRs automáticos atualizando dependências.

---

## Etapa 8 (segunda fase): incidentes e alertas

### O que mudou

- Duas tabelas novas: `incidents` (quando caiu, quando voltou, por quê) e `alert_notifications`
  (a "caixa de saída" dos alertas).
- `MonitoredApplication` ganhou `ConsecutiveFailures`.
- O agendador, depois de cada verificação, pergunta ao `IncidentTracker` se deve abrir ou fechar um
  incidente.
- Um segundo `BackgroundService`, o `AlertDispatcher`, envia os alertas.
- O dashboard ganhou um banner de incidentes abertos e uma tabela de incidentes no detalhe.

### Comandos

| Comando | O que faz |
|---|---|
| `dotnet ef migrations add AddIncidentsAndAlerts` | Gera a migration com as tabelas novas. Ela é aplicada sozinha quando a API sobe (`MigrateOnStartup`). |
| `git checkout -b feature/alertas` | Cria um branch para a funcionalidade. O código entra na `main` só pelo Pull Request, depois de revisado. |

### Conceitos

- **Máquina de estados.** O incidente tem dois estados (saudável e incidente aberto) e transições
  bem definidas. O `IncidentTracker` é uma função pura: recebe o estado atual e o resultado da
  verificação e devolve o novo estado. Por não acessar banco nem rede, é trivial de testar.
- **Limiar e flapping.** Redes falham por um instante o tempo todo. Se cada falha virasse alerta, o
  time passaria a ignorar os alertas (*alert fatigue*). Exigir N falhas seguidas troca alguns segundos
  de atraso por muito menos ruído.
- **Índice único parcial.** `CREATE UNIQUE INDEX ... ON incidents ("ApplicationId") WHERE
  "ResolvedAt" IS NULL`. Garante no banco que só existe um incidente aberto por aplicação, mesmo que o
  código tenha um bug ou que duas instâncias rodem ao mesmo tempo. Regra importante vira restrição do
  banco, não só `if` no código.
- **Transactional outbox.** O problema: gravar o incidente no banco e enviar a mensagem para o Slack
  são duas operações em sistemas diferentes, e não existe transação que cubra as duas. Se enviar
  primeiro e o banco falhar, o alerta fala de algo que não existe. Se gravar e o Slack estiver fora,
  o alerta se perde. A solução: gravar a *intenção* de enviar (a linha em `alert_notifications`) na
  mesma transação do incidente. Um processo separado lê essa tabela e envia. É o mesmo padrão usado
  para publicar eventos em filas (Kafka, RabbitMQ) em microsserviços.
- **At-least-once vs. exactly-once.** Entregar exatamente uma vez entre sistemas é praticamente
  impossível. Escolhemos "pelo menos uma vez": no pior caso, um alerta duplicado. Quem recebe pode
  usar o `incidentId` para ignorar duplicatas (*idempotência*).
- **Backoff exponencial.** Se o webhook falhou, tentar de novo imediatamente costuma falhar de novo
  e sobrecarrega quem já está com problema. Dobrar a espera a cada tentativa (30 s, 1 min, 2 min...)
  dá tempo para o serviço se recuperar, e um teto de tentativas evita tentar para sempre.
- **Webhook.** É só um `POST` HTTP com JSON para uma URL que o outro sistema fornece. Slack espera
  `{"text": "..."}`, Discord espera `{"content": "..."}`. O formato `Generic` manda todos os campos
  para integrações próprias.
- **`forkJoin` no Angular.** Dispara a busca de aplicações e de incidentes em paralelo e só atualiza
  a tela quando as duas respostas chegam.

## Etapa 9 (terceira fase): login e autenticação

### O que mudou

- Tabela nova `users` (e-mail único, hash da senha, papel, último login).
- Pasta `Auth/` no backend: login, logout, `/me`, gestão de usuários e emissão do token.
- Toda a API passou a exigir login. Escritas em aplicações e a gestão de usuários exigem `Admin`.
- No Angular: tela de login, guards nas rotas, interceptor para sessão expirada, botão Sair, tela
  Usuários e botões de escrita escondidos para Viewers.

### Comandos

| Comando | O que faz |
|---|---|
| `dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer` | Instala o middleware que lê e valida tokens JWT em cada requisição. |
| `dotnet ef migrations add AddUsers` | Gera a migration da tabela `users`. |
| `dotnet user-secrets set "Auth:JwtSigningKey" "..."` | Guarda a chave dos tokens no perfil do seu usuário, fora do repositório. |
| `openssl rand -base64 48` | Gera 48 bytes aleatórios em Base64: uma chave forte para assinar os tokens. |
| `curl -c cookies.txt` / `curl -b cookies.txt` | `-c` grava os cookies recebidos num arquivo e `-b` os reenvia. É o jeito de "logar" pelo terminal. |

### Conceitos

- **Autenticação x autorização.** Autenticação responde "quem é você?" (login). Autorização
  responde "você pode fazer isso?" (papel). Sem login a API responde `401 Unauthorized`; logado mas
  sem permissão, `403 Forbidden`.
- **Hash de senha (PBKDF2).** Nunca se guarda a senha, nem criptografada (criptografia se desfaz
  com a chave). Guarda-se um hash: uma função de mão única. O *salt* (bytes aleatórios por usuário)
  faz duas senhas iguais virarem hashes diferentes e inutiliza tabelas prontas (*rainbow tables*).
  PBKDF2 repete o hash centenas de milhares de vezes de propósito: para o usuário são milissegundos,
  para quem roubou o banco e quer testar bilhões de senhas, é lento demais. `SuccessRehashNeeded`
  atualiza o hash quando o padrão de iterações fica mais forte.
- **JWT (JSON Web Token).** Três partes em Base64 separadas por ponto: cabeçalho (algoritmo),
  *payload* (claims: `sub` = id do usuário, `email`, `role`, `exp` = expiração) e assinatura. Qualquer
  um consegue *ler* o payload; o que ninguém consegue sem a chave é *alterar* sem invalidar a
  assinatura. Por isso nunca se coloca segredo dentro do JWT.
- **Por que fixar o algoritmo (`ValidAlgorithms`).** Um ataque clássico manda um token com
  `"alg": "none"` (sem assinatura). Aceitando só HMAC-SHA256, esse token é recusado. Há um teste
  que tenta exatamente isso.
- **Stateless e o problema da revogação.** O servidor não guarda sessão: valida a assinatura e
  pronto. A desvantagem é que um token roubado ou de alguém demitido valeria até expirar. Resolvi
  com uma consulta barata por chave primária a cada requisição (`OnTokenValidated`): se o usuário
  sumiu ou mudou de papel, o token é recusado na hora. É um meio-termo entre JWT puro e sessão no
  servidor.
- **Cookie `HttpOnly` em vez de `localStorage`.** Token no `localStorage` pode ser lido por qualquer
  script da página, então um único XSS rouba a sessão. Com `HttpOnly`, o JavaScript não enxerga o
  cookie e o navegador o envia sozinho. `Secure` = só em HTTPS. `SameSite=Strict` = o navegador não
  envia o cookie quando a requisição nasce em outro site, o que bloqueia CSRF. `Path=/api` = o cookie
  só vai para a API.
- **Fallback policy (fechado por padrão).** Em vez de lembrar de proteger cada endpoint, todos
  exigem login e os públicos dizem `AllowAnonymous()`. Esquecer algo deixa o sistema mais fechado,
  nunca mais aberto.
- **RBAC (controle por papel).** Dois papéis bastam aqui: `Admin` e `Viewer`. Novos usuários nascem
  `Viewer` (menor privilégio). O frontend esconde botões, mas quem garante é a API: o cliente nunca
  é confiável.
- **Enumeração de usuários.** Se o login dissesse "e-mail não encontrado", um atacante descobriria
  quem tem conta. A mensagem é genérica, e a API calcula um hash mesmo quando o e-mail não existe
  para que o *tempo* de resposta também não denuncie.
- **Força bruta.** Rate limit de 5 tentativas por minuto por IP só no login, bem mais restrito que o
  resto da API.
- **Admin inicial (bootstrap).** Alguém precisa existir para criar os outros. Na primeira subida,
  com o banco vazio, a API cria o admin a partir de variáveis de ambiente. Depois disso elas são
  ignoradas.
- **Guards e interceptors no Angular.** *Guard* é uma função que o router chama antes de abrir uma
  rota: devolve `true` ou um redirecionamento (para `/login?returnUrl=...`). *Interceptor* é um
  "filtro" por onde passa toda requisição HTTP: se a API responder `401` (sessão expirou), ele limpa
  o estado e manda para o login. Detalhe que um teste pegou: `inject()` só funciona antes do
  primeiro `await` dentro de um guard.
- **Open redirect.** O `returnUrl` vem da URL, então um link malicioso poderia mandar o usuário para
  outro site depois do login. `safeReturnUrl` só aceita caminhos que começam com uma única `/`.

## Etapa 10 (quarta fase): registro de deploys

### O que mudou

- Tabelas novas: `deployments` (versão, commit, link, quando, quem registrou) e `api_keys`.
- Um segundo jeito de autenticar, só para máquinas: **chave de API** no cabeçalho
  `Authorization: Bearer dg_...`.
- `POST /api/applications/{id}/deployments` aceita um Admin logado **ou** uma chave de API.
- Incidentes ganharam `suspectedDeployment`, e o alerta do Slack passou a dizer
  "Último deploy: v1.4.2, 3 min antes".
- No Angular: tabela de deploys e formulário no detalhe, linhas tracejadas no gráfico nos momentos
  de deploy, coluna "Deploy suspeito" nos incidentes e a tela **Chaves de API**.
- Exemplo de workflow em `docs/exemplos/avisar-deploy.yml`.

### Comandos

| Comando | O que faz |
|---|---|
| `dotnet ef migrations add AddDeploymentsAndApiKeys` | Gera a migration das duas tabelas novas e do índice `(ApplicationId, DeployedAt)`. |
| `curl -H "Authorization: Bearer dg_..."` | Manda a chave de API no cabeçalho padrão de autenticação HTTP. É assim que o CI se identifica. |
| `jq -n --arg ... '{...}'` | No workflow, monta o JSON a partir de variáveis. Escapa aspas e quebras de linha (a mensagem do commit pode ter as duas). Montar JSON concatenando texto é um bug clássico. |
| `${GITHUB_SHA::7}` | Sintaxe do bash: os 7 primeiros caracteres do hash do commit, o "hash curto". |

### Conceitos

- **Por que correlacionar deploy e incidente.** Estudos de confiabilidade (como os relatórios DORA)
  mostram que a maior parte dos incidentes vem de mudanças: deploys e configuração. Mostrar o
  deploy logo antes da queda encurta o MTTR (tempo médio até recuperar), porque o time já sabe
  o que reverter. Chamamos de **suspeito**, não de **causa**: correlação não é causalidade.
- **Autenticação de pessoas x de máquinas.** Pessoas fazem login com senha e recebem um cookie.
  Um pipeline de CI não tem navegador nem pode digitar senha, então recebe uma credencial própria:
  a chave de API. São dois **esquemas de autenticação** no ASP.NET Core (`JwtBearer` e `ApiKey`),
  e a política `deployment-writer` aceita qualquer um dos dois.
- **Menor privilégio.** A chave só vale para o endpoint de registrar deploy. Os outros endpoints
  usam só o esquema do cookie, então para eles a chave nem é lida, e a resposta é `401`. Se a chave
  vazar, o estrago máximo é alguém registrar um deploy falso, não ler nem apagar dados.
- **Por que SHA-256 serve para a chave, mas não para a senha.** A senha de uma pessoa é fraca e
  previsível, então precisa de hash **lento** (PBKDF2) para atrasar quem tenta adivinhar. A chave
  de API tem 256 bits aleatórios: não dá para adivinhar nem com hash rápido. E o hash rápido
  importa, porque a chave é conferida em toda chamada.
- **Mostrar a chave uma vez só.** Como guardamos apenas o hash, nem o servidor sabe mais a chave
  depois de criá-la. É o mesmo modelo do GitHub (tokens) e da AWS (access keys). Perdeu? Revoga e
  cria outra. O prefixo `dg_` ajuda ferramentas de varredura de segredos a reconhecer uma chave
  vazada num repositório.
- **Auditoria.** Cada deploy guarda quem o registrou (`admin@...` ou `chave: github-actions`).
  Cada chave guarda quem a criou e quando foi usada pela última vez, o que ajuda a achar chaves
  esquecidas.
- **XSS por link.** O Angular escapa texto, mas um `href="javascript:alert(1)"` executaria código
  ao clicar. Por isso a API só aceita links `https`, e há teste para `javascript:` e `data:`.
- **Subconsulta correlacionada.** Na listagem de incidentes, o deploy suspeito de cada linha é
  buscado dentro da mesma consulta SQL (o EF Core gera um `LEFT JOIN LATERAL` no PostgreSQL). Uma
  consulta separada por incidente seria o problema **N+1**: 50 incidentes, 51 idas ao banco.
- **Gráfico com eixo por ordem, marca por tempo.** O eixo X do gráfico é a ordem das verificações.
  Para desenhar o deploy no lugar certo, o componente acha as duas verificações em volta do horário
  do deploy e **interpola** a posição entre elas.
- **Bug encontrado no teste ponta a ponta.** O proxy do `ng serve` encaminhava tudo que começava
  com `/api`, inclusive a página `/api-keys` do Angular, que caía no backend e voltava 401. A
  correção foi trocar a rota para `/keys` e o proxy para `/api/` (com barra). Teste unitário não
  pega esse tipo de erro de integração.

## Etapa 11 (quinta fase): painel em tempo real

### O que mudou

- Endpoint novo `GET /api/events`, que fica aberto e manda avisos no formato **Server-Sent
  Events**: `check` (verificação terminou), `incident-opened`, `incident-resolved`, `deployment`,
  além de `ready` (conectou) e `ping` (sinal de vida).
- Pasta `Live/` no backend: `LiveEventHub` (central de avisos em memória), `LiveEvent` (o aviso),
  `LiveOptions` (limites) e `LiveEndpoints` (o endpoint SSE).
- O agendador e o registro de deploy publicam avisos **depois** de gravar no banco.
- No Angular: `LiveEventsService` (`core/live-events.ts`) e o indicador "● Ao vivo" no dashboard.
  O dashboard e o detalhe recarregam quando chega um aviso; o polling caiu de 15 s para 60 s e
  virou só rede de segurança.
- nginx: um `location = /api/events` com `proxy_buffering off`.

### Comandos

| Comando | O que faz |
|---|---|
| `curl -N -b cookies.txt http://localhost:8080/api/events` | Abre o canal ao vivo no terminal. `-N` desliga o buffer do curl, para cada evento aparecer na hora em que chega. `-b` manda o cookie de login. `Ctrl+C` fecha. |
| `TypedResults.ServerSentEvents(...)` | (C#) Novo no .NET 10. Recebe um `IAsyncEnumerable` e escreve cada item no formato SSE (`event:`, `data:`, linha em branco). |

### Conceitos

- **Polling x push.** Antes, o navegador perguntava a cada 15 s "mudou algo?" (polling). Quase
  sempre a resposta era "não", e quando mudava, a tela demorava até 15 s. Agora o servidor
  **avisa** quando muda (push). A tela atualiza em menos de 1 s e faz menos requisições à toa.
- **SSE x WebSocket.** Os dois mantêm uma conexão aberta. WebSocket é nos dois sentidos e é outro
  protocolo. SSE é uma resposta HTTP normal que não termina, só do servidor para o navegador. Para
  um painel que só recebe, SSE basta e é mais simples: usa o mesmo cookie, passa pelo mesmo proxy e
  o navegador (`EventSource`) reconecta sozinho.
- **O formato na rede** é texto puro:

  ```
  event: check
  data: {"type":"CheckCompleted","applicationId":"...","status":"Down"}

  ```

  A linha em branco separa uma mensagem da outra. `retry: 3000` diz ao navegador para esperar 3 s
  antes de reconectar.
- **Aviso, não dado.** O evento diz só "a aplicação X mudou". A tela busca os dados completos na
  API, como antes. Assim existe uma fonte de verdade só (o banco). Se um aviso se perder, nada
  fica errado para sempre: o próximo aviso ou a atualização de 1 minuto corrige.
- **Publish/subscribe e `Channel`.** O `LiveEventHub` guarda uma fila (`Channel<T>`) por conexão.
  Publicar = colocar o aviso em todas as filas. Cada conexão lê a sua no próprio ritmo. A fila é
  **limitada** (100) e, cheia, descarta o mais antigo (`DropOldest`): um cliente com rede ruim não
  faz a memória do servidor crescer sem fim, e quem publica (o agendador) nunca fica esperando.
- **`IAsyncEnumerable` e `yield return`.** O endpoint é um método que "vai devolvendo" itens aos
  poucos, com `await` no meio. O ASP.NET Core escreve cada item na resposta assim que ele sai. Quando
  o navegador fecha a aba, o `CancellationToken` é cancelado, o loop termina e o `using` libera a vaga.
- **Heartbeat (`ping`).** Proxies e balanceadores fecham conexões paradas (o nginx, por padrão, em
  60 s sem dados). O `ping` a cada 15 s mantém a conexão viva e ajuda o navegador a perceber se ela
  caiu.
- **Duração máxima e reautenticação.** O login é conferido quando a conexão abre. Se a conexão
  durasse para sempre, um usuário removido continuaria recebendo avisos. Por isso o servidor fecha
  a cada 5 minutos; o navegador reconecta e passa pelo login de novo.
- **Limite de conexões e degradação elegante.** Cada conexão aberta ocupa memória e um socket. Acima
  de 100, a API responde `503`, e a tela mostra "Reconectando…" e continua com a atualização de
  1 minuto. O sistema fica mais lento, mas não quebra.
- **`Interlocked`.** Duas conexões chegando ao mesmo tempo poderiam passar as duas do limite se o
  código fizesse "lê o contador, compara, soma". `Interlocked.Increment` faz a soma de forma
  **atômica**: só uma thread de cada vez.
- **Buffering no nginx.** Por padrão o nginx junta a resposta do backend antes de repassar. Para
  uma página isso é bom; para SSE, os eventos ficariam presos. `proxy_buffering off` repassa cada
  pedaço na hora.
- **`auditTime` e `share` (RxJS).** Uma rodada do agendador manda vários avisos seguidos. O
  `auditTime(500)` espera meio segundo e dispara uma busca só. O `share` faz o dashboard e o detalhe
  usarem a **mesma** conexão, em vez de abrir uma por tela.
- **Testar o tempo sem esperar.** O `FakeTimeProvider` é um relógio falso: o teste chama
  `Advance(15 s)` e o `ping` sai na hora, sem o teste dormir 15 segundos. Por isso o endpoint recebe
  o `TimeProvider` em vez de usar `DateTime.Now`.
- **Limitação: uma instância só.** A central de avisos vive na memória do processo. Com duas
  instâncias da API atrás de um balanceador, um navegador conectado na instância A não veria os
  avisos da B. A solução seria um barramento compartilhado, como o `LISTEN/NOTIFY` do PostgreSQL ou
  Redis pub/sub.

## Etapa 12 (sexta fase): várias instâncias da API

### O que mudou

- O `docker-compose.yml` sobe 2 réplicas da API (`API_REPLICAS`). O nginx reparte as requisições.
- `HealthCheckScheduler` e `AlertDispatcher` reservam lotes com `UPDATE ... FOR UPDATE SKIP LOCKED
  ... RETURNING *` em vez de um `SELECT` simples.
- `LiveEventBus` (publica com `pg_notify`) e `LiveEventListener` (faz `LISTEN` e entrega ao
  `LiveEventHub` local) levam os avisos ao vivo a todas as instâncias.
- `DatabaseStartup.MigrateAsync`: migrations protegidas por `pg_advisory_lock`.
- A criação do admin inicial tolera a corrida entre duas instâncias.

### Comandos

| Comando | O que faz |
|---|---|
| `docker compose ps` | Lista os containers. Com réplicas, aparecem `api-1` e `api-2`. |
| `docker compose logs -f api` | Segue (`-f`) o log de todas as réplicas. Cada linha começa com o nome da réplica, então dá para ver quem verificou o quê. |
| `API_REPLICAS=1` no `.env` | Volta a subir uma réplica só. No compose, `${API_REPLICAS:-2}` quer dizer "use a variável ou 2 se ela não existir". |
| `SELECT pg_notify('canal', 'texto')` | (SQL) Manda uma notificação para quem estiver escutando o canal. |
| `LISTEN canal` | (SQL) Passa a receber as notificações do canal nesta conexão. |

### Conceitos

- **Escala horizontal x vertical.** Vertical = máquina maior. Horizontal = mais cópias do mesmo
  programa. A horizontal também dá **alta disponibilidade**: se uma réplica cai, a outra continua.
- **Por que a API escala fácil e o agendador não.** As requisições HTTP não guardam estado na memória
  (o login é um JWT no cookie, conferido em qualquer réplica). Já o agendador "decide" o que fazer:
  com duas cópias, as duas decidiriam verificar a mesma aplicação, e o Slack receberia o alerta em
  dobro.
- **Condição de corrida (*race condition*).** Duas réplicas leem "app X vencida" ao mesmo tempo e as
  duas a verificam. O problema está no intervalo entre **ler** e **marcar**. A solução é ler e
  marcar num único comando atômico.
- **`FOR UPDATE SKIP LOCKED`.** `FOR UPDATE` trava as linhas lidas até o fim da transação. `SKIP
  LOCKED` faz a outra réplica **pular** as linhas travadas em vez de esperar. Resultado: cada uma
  pega um pedaço diferente, sem fila e sem duplicar. É o jeito clássico de usar uma tabela do
  PostgreSQL como fila de trabalho.
- **Lease (prazo de reserva).** Não dá para manter a trava aberta durante as chamadas HTTP (segundos
  de transação aberta seguram conexões e travas). Então a reserva só empurra `NextCheckAt` para o
  futuro e a transação termina na hora. Se a réplica morrer antes de gravar, o prazo vence e outra
  pega. É a mesma ideia do *visibility timeout* do Amazon SQS.
- **Pelo menos uma vez, de novo.** Se a réplica enviar o alerta e cair antes de gravar `SentAt`, o
  prazo vence e outra réplica reenvia. Duplicar raramente é aceitável; perder, não.
- **`LISTEN/NOTIFY`.** Um pub/sub embutido no PostgreSQL. Cada réplica abre uma conexão própria que
  fica "escutando". O `NOTIFY` é **transacional**: só sai no `COMMIT`. Por isso os avisos vão na
  mesma transação dos dados, e nunca anunciam algo que foi desfeito. Limite: 8000 bytes por
  mensagem e nenhuma garantia de entrega se a réplica estiver desconectada. Por isso o aviso é só
  um "algo mudou" e as telas têm a atualização de segurança.
- **Advisory lock (trava consultiva).** Uma trava com um número escolhido por nós, que não está
  ligada a nenhuma tabela. Usamos para "só uma réplica aplica migrations por vez". A outra espera,
  depois vê que não falta nada e segue.
- **Dois bugs que só apareceram rodando de verdade.** Os testes passavam, mas ao subir 2 réplicas
  no Compose: (1) as duas aplicaram migrations ao mesmo tempo e uma quebrou com `column already
  exists`; (2) as duas tentaram criar o admin inicial. A correção do primeiro é a advisory lock; a
  do segundo é tratar a violação de e-mail único como "outra réplica já criou". Lição: concorrência
  precisa ser testada com concorrência de verdade.
- **Testando duas instâncias.** O teste sobe dois hosts da API (`WithWebHostBuilder`) no mesmo
  banco e roda os dois agendadores ao mesmo tempo com um health check falso lento (100 ms), para
  as rodadas se sobreporem. Para provar que o teste vale, troquei a reserva por um `SELECT` simples:
  o teste falhou, com aplicações verificadas duas vezes.
- **O que ficou por instância.** O rate limit e o limite de conexões ao vivo vivem na memória de
  cada réplica, então com 2 réplicas o teto dobra. Um limite global precisaria de um contador
  compartilhado, como o Redis.

## Etapa 13 (sétima fase): conta e segurança do login

### O que mudou

- `users` ganhou `FailedLoginCount`, `LockoutEnd` e `SecurityStamp`. Tabela nova `audit_events`.
- `POST /api/auth/password` (troca de senha), `POST /api/users/{id}/unlock` e `GET /api/audit`.
- O token passou a levar o claim `stamp`, conferido a cada requisição junto com o papel.
- No Angular: tela **Minha conta** (clicando no seu e-mail no topo), situação "Bloqueado até" com
  botão **Desbloquear** em Usuários, e a tela **Auditoria**.

### Comandos

| Comando | O que faz |
|---|---|
| `dotnet ef migrations add AddAccountSecurityAndAudit` | Gera a migration com as colunas novas de `users` e a tabela `audit_events`. |
| `defaultValueSql: "gen_random_uuid()"` | (na migration) Usuários que já existiam ganham um carimbo aleatório cada um, em vez de todos com zeros. |
| `curl -b cookies.txt "http://localhost:8080/api/audit?action=LoginFailed"` | Lista só os logins recusados: o primeiro lugar para olhar quando se suspeita de ataque. |

### Conceitos

- **Duas camadas contra força bruta.** O rate limit por IP para um robô numa máquina só. O
  bloqueio por conta para um ataque **distribuído** (milhares de IPs testando a senha de uma conta).
  Uma camada cobre o buraco da outra.
- **O dilema do bloqueio.** Bloquear para sempre deixaria qualquer um trancar a conta do chefe
  errando a senha de propósito (negação de serviço). Por isso o bloqueio dura 15 minutos e o Admin
  pode desbloquear. Durante o bloqueio, as tentativas não estendem o prazo.
- **Não revelar nada.** E-mail inexistente, senha errada e conta bloqueada devolvem exatamente a
  mesma resposta, no mesmo tempo. Se a API dissesse "conta bloqueada", o atacante descobriria que
  aquele e-mail existe. A mensagem avisa sobre o bloqueio de forma genérica, para todo mundo.
- **UPDATE atômico no contador.** `SET FailedLoginCount = FailedLoginCount + 1` roda no banco, numa
  instrução só. Ler o valor, somar no C# e gravar perderia tentativas simultâneas (mesma condição de
  corrida da etapa 12). Há um teste que manda 5 tentativas ao mesmo tempo.
- **Security stamp (carimbo de segurança).** Um JWT não pode ser apagado. Então guardamos no
  usuário um valor aleatório que também vai no token. Trocar a senha troca o valor, e todo token
  antigo deixa de bater na conferência que já fazíamos a cada requisição. É o mesmo mecanismo do
  ASP.NET Core Identity.
- **Por que trocar a senha derruba as outras sessões.** Se alguém descobriu sua senha e entrou, você
  troca a senha e o invasor cai na hora. A sua sessão continua porque a API devolve um cookie novo.
- **Auditoria "só de inserção".** O registro entra no mesmo `SaveChanges` da ação: não existe
  "usuário criado" sem o registro, nem registro de algo que falhou. A API não tem rota para editar
  ou apagar. Guarda o e-mail como texto, para continuar legível depois que o usuário for removido.
- **O que NUNCA vai para o log.** Senha (nem a errada, que costuma ser a certa com um erro de
  digitação), chave de API e token. Há teste que confere que a senha digitada não aparece.
- **Dado pessoal.** IP e e-mail são dados pessoais (LGPD). Em produção, a auditoria precisa de prazo
  de retenção e acesso restrito, como já está (só Admin).
- **O rate limit pegou o próprio teste.** No teste no navegador, as 5 senhas erradas esbarraram
  primeiro no limite de 5 logins por minuto por IP ("Muitas requisições"). Era a primeira camada
  funcionando. Para testar a segunda, subi a API local com um limite maior.

## Etapa 14 (oitava fase): página de status pública

### O que mudou

- `applications` ganhou a coluna `IsPublic` (falso por padrão).
- Rota nova `GET /api/public/status`, sem login, com output cache de 30 s e rate limit próprio.
- No Angular: página `/status` (fora do `authGuard`), botão **Publicar na página de status** na
  tela da aplicação e link "Status" no topo e na tela de login.

### Comandos

| Comando | O que faz |
|---|---|
| `dotnet ef migrations add AddPublicStatusPage` | Gera a migration que adiciona `IsPublic` em `applications`. |
| `curl -i http://localhost:8080/api/public/status` | Chama a rota sem cookie, como um visitante. O `-i` mostra os cabeçalhos, incluindo `Cache-Control: public, max-age=30`. |
| `("CheckedAt" AT TIME ZONE 'UTC')::date` | (SQL) Converte o horário para UTC e fica só com a data. É a chave do `GROUP BY` por dia. |
| `count(*) FILTER (WHERE "Status" <> 'Down')` | (SQL) Conta só as linhas que passam no filtro, dentro do mesmo `GROUP BY`. |

### Conceitos

- **Superfície pública mínima.** Tudo o que uma rota anônima devolve está na internet. Por isso ela
  tem um DTO próprio, bem menor que o da área logada: sem `Id`, sem URL, sem latência e sem
  mensagem de erro. Uma mensagem como `connect 10.0.0.5:443 timed out` revelaria a rede interna.
- **Opt-in.** Nada é público até o Admin decidir. O padrão seguro é "fechado".
- **Output cache.** O ASP.NET Core guarda a resposta pronta em memória por 30 s. Mil visitantes no
  mesmo minuto viram duas consultas ao banco, não mil. Respostas para usuários logados não entram
  no cache (é o comportamento padrão, para não servir dado de um usuário a outro).
- **`Cache-Control: public, max-age=30`.** Diz ao navegador (e a uma CDN, se houver) que pode
  reaproveitar a resposta por 30 s. O resto da API manda `no-store`.
- **Status não é o mesmo que último resultado.** Uma falha isolada vira "Instável". "Fora do ar"
  só com incidente aberto, que exige várias falhas seguidas. Isso evita alarmar clientes à toa.
- **Polling em vez de SSE.** O canal ao vivo exige login. Como a resposta tem cache de 30 s,
  atualizar a cada minuto não pesa.
- **Datas em UTC.** O servidor agrupa por dia em UTC para todo mundo ver as mesmas barras,
  independentemente do fuso.

## Etapa 15 (nona fase): esqueci minha senha

### O que mudou

- Tabela nova `password_reset_tokens` (só o hash do token, validade e quando foi usado).
- `POST /api/auth/forgot-password` e `POST /api/auth/reset-password`, ambas públicas.
- Pasta `Email/`: envio por SMTP com o **MailKit**, fila em memória (`Channel`) e um
  `BackgroundService` que envia com até 3 tentativas.
- Docker Compose com o **Mailpit**, uma caixa de entrada de teste em `http://localhost:8025`.
- No Angular: telas **Esqueci minha senha** e **Criar nova senha**, e o link na tela de login.

### Comandos

| Comando | O que faz |
|---|---|
| `dotnet add package MailKit` | Adiciona a biblioteca de e-mail. A versão 4.18.1 foi recusada no build: o NuGet Audit apontou uma vulnerabilidade numa dependência dela, e o `TreatWarningsAsErrors` transformou o aviso em erro. A 4.18.2 já vem corrigida. |
| `dotnet ef migrations add AddPasswordReset` | Gera a migration da tabela `password_reset_tokens`. |
| `docker compose up` e abrir `http://localhost:8025` | Interface do Mailpit, onde aparecem os e-mails que a API mandou. |
| `curl -X POST localhost:8080/api/auth/forgot-password -H 'Content-Type: application/json' -d '{"email":"x@y.com"}'` | Pede o link. Teste com um e-mail que existe e com um que não existe: a resposta é idêntica. |

### Conceitos

- **Enumeração de contas, de novo.** Se a tela dissesse "e-mail não encontrado", qualquer um
  descobriria quem tem conta. A resposta é sempre a mesma, e o envio fica para depois, numa fila, para
  o **tempo** de resposta também não denunciar.
- **Token como senha temporária.** Quem tem o link entra na conta. Por isso ele é aleatório de
  verdade (`RandomNumberGenerator`, 256 bits), curto (30 min), de uso único e guardado só como hash.
- **Por que SHA-256 e não PBKDF2 como na senha?** Senha é escolhida por gente e pode ser fraca, então
  precisa de um hash lento. O token tem 256 bits aleatórios: ninguém adivinha por força bruta, e um
  hash rápido basta.
- **Uso único com UPDATE atômico.** `UPDATE ... SET UsedAt = agora WHERE Id = ... AND UsedAt IS NULL`.
  O banco garante que só uma requisição altera a linha. É a mesma ideia do contador de senhas erradas
  e do `SKIP LOCKED`.
- **Fragmento da URL (`#`).** Não é enviado ao servidor. O token não fica em logs de acesso, em
  proxies nem no cabeçalho `Referer`.
- **Host header injection.** Nunca montar links de e-mail com o `Host` da requisição: quem faz a
  requisição escolhe esse valor.
- **E-mail bombing.** Sem limite por usuário, um robô usaria o DeployGuard para encher a caixa de
  alguém. Limite: 3 pedidos por hora por conta, além do limite por IP.
- **Mailpit.** Um servidor SMTP falso para desenvolvimento: recebe tudo e mostra numa página web, sem
  mandar nada para a internet.
- **NuGet Audit.** O `dotnet restore` compara os pacotes com a base de vulnerabilidades do GitHub.
  Com `TreatWarningsAsErrors`, uma dependência vulnerável quebra o build antes de chegar à produção.

## Etapa 16: dependências em dia

### O que mudou

- Os 10 PRs abertos do Dependabot foram aplicados juntos num PR só e validados de uma vez:
  actions do GitHub (`checkout@v7`, `setup-node@v7`, `setup-dotnet@v6`, `upload-artifact@v7`),
  Node 26 no build do frontend, nginx 1.31, pacotes de teste do .NET (xUnit runner 4,
  Test SDK 18, coverlet 10) e correções do Angular 22.2.4.
- O **TypeScript 7 ficou de fora**: o `@angular/build` 22 exige `typescript >=6.0 <6.1` (dependência
  "peer"), e o `npm ci` recusa a instalação com `ERESOLVE`. O `dependabot.yml` agora ignora saltos de
  versão do TypeScript, que sobe junto com o Angular pelo `ng update`.
- O Dependabot passou a agrupar as actions e os pacotes de teste, para abrir menos PRs.

### Comandos

| Comando | O que faz |
|---|---|
| `npm outdated` | Lista os pacotes com versão mais nova disponível (atual, desejada pela faixa do `package.json` e a mais recente). |
| `npm update` | Atualiza dentro das faixas do `package.json` (ex.: `^22.2.3` aceita `22.2.4`), mexendo só no `package-lock.json`. |
| `dotnet list package --outdated` | O mesmo para os pacotes NuGet. |
| `docker run --rm -v $PWD:/app -w /app node:26-bookworm-slim npx ng build` | Testa o build numa versão do Node que não está instalada na máquina, usando um container descartável. |

### Conceitos

- **Peer dependency.** Um pacote declara "funciono com a versão X de outro pacote, que você instala".
  O Angular fixa a faixa do TypeScript porque o compilador dele usa a API interna do TypeScript.
- **Por que juntar os PRs?** Dez PRs separados significam dez rodadas de CI e dez merges, e um pode
  quebrar o outro. Num PR só, o CI valida a combinação que vai de fato para a `main`. Depois do merge,
  o Dependabot fecha sozinho os PRs que ficaram sem efeito.
- **Versão major de action.** `actions/checkout@v7` é uma tag que acompanha correções da v7. Em
  projetos mais rígidos, fixa-se o SHA do commit (proteção contra uma tag ser trocada por código
  malicioso).

## Etapa 17: rate limit global com Redis

### O que mudou

- Serviço `redis` no Docker Compose, com senha (`REDIS_PASSWORD`), sem porta exposta e sem disco.
- `Infrastructure/RedisRateLimiter.cs`: um `RateLimiter` do .NET que conta no Redis, com o limitador
  em memória de reserva.
- As quatro políticas (leitura, escrita, login e página pública) passam a usar o Redis quando
  `RateLimiting:Redis` está configurado.

### Comandos

| Comando | O que faz |
|---|---|
| `docker compose exec redis sh -c 'REDISCLI_AUTH=$REDIS_PASSWORD redis-cli --scan --pattern "deployguard:*"'` | Lista os contadores. Cada chave é `deployguard:ratelimit:<política>:<ip>`. |
| `redis-cli GET <chave>` / `redis-cli TTL <chave>` | Mostra quantas requisições o IP fez na janela e quantos segundos faltam para zerar. |
| `docker compose stop redis` | Simula o Redis caindo. A API continua respondendo, com um aviso no log. |

### Conceitos

- **Por que global?** Com 2 réplicas atrás do nginx, cada uma contava sozinha: o limite de 5
  logins por minuto virava 10. Com o contador no Redis, as duas somam no mesmo lugar.
- **INCR atômico com Lua.** O script soma 1 e, na primeira requisição da janela, define a expiração,
  tudo numa operação só. Se fossem dois comandos separados, uma queda entre eles deixaria uma chave
  sem prazo (o IP ficaria bloqueado para sempre). O script também corrige uma chave sem prazo, se
  existir.
- **Fail-open para o local, não para "sem limite".** Se o Redis cair, a API não para (o login não
  depende do Redis) e também não fica sem freio: cada réplica volta a usar o contador em memória.
- **Fail fast.** Desconectado, o cliente do Redis falha na hora (`BacklogPolicy.FailFast`) em vez de
  esperar o timeout. No teste, a resposta continuou em ~1 ms com o Redis desligado.
- **Timeouts curtos (500 ms).** Um Redis lento não pode segurar todas as requisições da API.
- **Por que Redis e não o PostgreSQL?** Daria para contar no banco, mas cada requisição viraria uma
  escrita no PostgreSQL. O Redis guarda tudo em memória e faz milhares de `INCR` por segundo. Os
  contadores são descartáveis (expiram em 1 minuto), então não precisam de disco nem de backup.
- **Senha mesmo na rede interna.** Defesa em profundidade: se outro container da rede for
  comprometido, ainda precisa da senha.

## Etapa 18: bloqueio por conta e IP (correção da auditoria)

### O que mudou

- A auditoria de segurança achou o **SEC-001**: o bloqueio depois de 5 senhas erradas era da conta
  inteira. Quem soubesse o e-mail do Admin errava a senha 5 vezes a cada 15 minutos e o deixava
  trancado para sempre, sem precisar de login nenhum.
- Tabela nova `login_failures`, com uma linha por par (conta, IP): 5 erradas do mesmo IP bloqueiam
  só esse IP nessa conta.
- O bloqueio da conta inteira continua existindo, mas com um teto bem mais alto
  (`Auth:MaxFailedLoginsPerAccount`, padrão 50), para frear ataques espalhados por muitos IPs.
- O desbloqueio pelo Admin e o reset de senha liberam também os IPs. O agendador apaga registros com
  mais de um dia.

### Comandos

| Comando | O que faz |
|---|---|
| `dotnet ef migrations add AddLoginFailuresPerIp` | Gera a migration que cria a tabela nova a partir das mudanças no `DbContext`. |
| `git stash push -u -- backend/src` | Guarda as mudanças de lado. Serviu para rodar o teste novo no código antigo e ver ele falhar. |
| `git stash pop` | Traz as mudanças de volta. |

### Conceitos

- **Upsert (`INSERT ... ON CONFLICT DO UPDATE`).** Cria a linha na primeira falha e soma nas
  seguintes, num único comando. Duas requisições ao mesmo tempo não criam linhas duplicadas nem
  perdem contagem, porque o PostgreSQL resolve o conflito na chave primária (conta, IP).
- **`RETURNING`.** Faz o próprio `INSERT/UPDATE` devolver um valor (aqui: "ficou bloqueado?"), sem
  uma segunda consulta.
- **Por que não só por IP?** Um atacante com muitos IPs (uma botnet) poderia testar 4 senhas de cada
  um, sem nunca bloquear. O teto por conta fecha essa porta.
- **Teste de regressão.** Primeiro o teste foi rodado no código antigo e falhou (prova que ele pega
  o problema), depois no código novo e passou.
- **Simular IP nos testes.** O servidor de teste não tem IP de cliente. Um `IStartupFilter` só dos
  testes lê o cabeçalho `X-Test-Client-Ip` e define o IP da requisição.

## Etapa 19: e-mail só com TLS (correção da auditoria)

### O que mudou

- A auditoria achou o **SEC-002**: o compose usa `SMTP_SECURITY=None` por padrão (para o Mailpit).
  Quem configurasse um SMTP de verdade e esquecesse essa variável mandaria a senha do SMTP e o link
  de "esqueci minha senha" sem criptografia.
- Agora a API **não sobe** nesse caso. Sem TLS só vale para um servidor local de teste
  (`localhost`, loopback ou nome sem ponto, como `mailpit`) e sem usuário/senha.
- O README ganhou a seção "Antes de expor fora da sua máquina" (HTTPS, SMTP real, `DASHBOARD_URL`),
  que era a observação INF-007 da auditoria.

### Conceitos

- **STARTTLS e ataque de downgrade.** No SMTP, a conexão começa em texto puro e o cliente pede
  "vamos para TLS" (STARTTLS). Se o servidor não oferecer, os modos `Auto` e
  `StartTlsWhenAvailable` seguem **sem** criptografia. Um atacante no meio do caminho só precisa
  apagar a palavra STARTTLS da resposta do servidor. Por isso só `StartTls` (exige) e
  `SslOnConnect` (TLS desde o primeiro byte) são aceitos.
- **Falhar na subida (fail fast).** É melhor a API não subir, com uma mensagem clara, do que subir
  e vazar segredos em silêncio. É o mesmo padrão da chave JWT curta.
- **Configuração segura por padrão.** O valor padrão serve para o caso de teste, mas a validação
  impede que ele seja usado no caso real.

---

## Perguntas de entrevista

**Explique o projeto em 30 segundos.**
É uma plataforma de monitoramento: você cadastra aplicações com uma URL de health check, um serviço
em segundo plano verifica cada uma periodicamente, grava disponibilidade, latência e erros no
PostgreSQL, e um dashboard Angular mostra o estado atual e o histórico. A parte mais interessante é
a segurança, porque fazer requisições para URLs do usuário é um vetor clássico de SSRF.

**O que é SSRF e como você mitigou?**
É quando o atacante faz o servidor requisitar um destino interno. Mitiguei em camadas: allowlist de
hosts, bloqueio de IPs privados/loopback/link-local, validação do IP no momento da conexão (contra
DNS rebinding), sem seguir redirects, sem proxy, timeouts e limites de volume.

**Por que validar o IP na conexão e não só no cadastro?**
Por DNS rebinding: o mesmo nome pode resolver para outro IP depois. Validando no `ConnectCallback` e
conectando nos IPs validados, não há intervalo entre checar e usar.

**Como o agendador funciona? E se houver duas instâncias da API?**
`BackgroundService` com `PeriodicTimer`. A cada tick, busca quem tem `NextCheckAt` vencido, roda em
paralelo com teto e grava tudo numa transação. Com duas instâncias, as duas verificariam as mesmas
aplicações. A solução seria `SELECT ... FOR UPDATE SKIP LOCKED` para cada instância "pegar" um lote
diferente, ou mover o agendador para um worker único.

**Por que não usar o `DbContext` dentro do `Parallel.ForEachAsync`?**
Porque o `DbContext` não é thread-safe. As verificações paralelas só produzem resultados em memória;
a gravação acontece depois, numa thread.

**Por que paginação por cursor?**
`OFFSET` degrada com o tamanho da tabela e duplica ou pula itens quando entram dados novos. Keyset
usa o índice e é estável.

**Por que p95 e não só média?**
A média esconde picos. Se 5% das requisições levam 3 segundos, a média pode parecer ótima e o p95
mostra o problema.

**Como você protege segredos?**
Nada no repositório: variáveis de ambiente, `user-secrets` em desenvolvimento, `.env` ignorado pelo
git, Compose falha se a senha não for definida. Também não aceito credenciais dentro da URL e não
logo URLs completas.

**Como o rate limiting sabe o IP real atrás do nginx?**
Pelo `X-Forwarded-For`, mas só quando a requisição vem de uma rede conhecida (o nginx), e o nginx
sobrescreve o cabeçalho. Senão, qualquer cliente forjaria o IP para escapar do limite.

**Por que testar com PostgreSQL real?**
Porque provedores em memória não reproduzem o comportamento do banco (SQL gerado, tipos, fuso
horário). Testcontainers dá um banco descartável e isolado por execução.

**O que você faria a seguir?**
Autenticação (OIDC/JWT), alertas na mudança de estado, Server-Sent Events no dashboard, distribuir o
agendamento entre instâncias e, na visão do produto, OpenTelemetry e correlação com deploys.

**Como você evita perder um alerta se o Slack estiver fora do ar?**
Com o padrão transactional outbox: o alerta é gravado no banco na mesma transação do incidente, e um
serviço separado envia com retentativas e backoff exponencial. A falha do Slack só atrasa o alerta.

**E se o alerta for enviado duas vezes?**
Pode acontecer (entrega "pelo menos uma vez"), e é uma escolha consciente: duplicar é melhor que
perder. O `incidentId` no payload permite ao receptor descartar duplicatas.

**Como você evita alertas falsos?**
Com um limiar de falhas consecutivas antes de abrir o incidente, o que evita *flapping*. Também não
considero "lento" (Degraded) como queda.

**Como garante que não existem dois incidentes abertos para a mesma aplicação?**
Além da lógica no código, um índice único parcial no PostgreSQL. Se algo der errado, o banco recusa a
gravação.

**Onde você guarda o token de login no frontend e por quê?**
Num cookie `HttpOnly`, `Secure`, `SameSite=Strict`. O JavaScript não consegue lê-lo, então um XSS
não rouba a sessão, e o `SameSite` impede que outro site use o cookie (CSRF). O frontend descobre
quem está logado chamando `/api/auth/me`.

**JWT não pode ser revogado. Como você lida com isso?**
A cada requisição confiro, por chave primária, se o usuário do token ainda existe e tem o mesmo
papel. Remover alguém corta o acesso na hora. Em escala maior, daria para trocar por um cache ou
por uma lista de tokens revogados com o `jti`.

**Como as senhas são guardadas?**
Hash PBKDF2 com salt aleatório, pelo `PasswordHasher` do ASP.NET Core Identity. A senha nunca é
gravada nem devolvida. A política exige comprimento (12+) em vez de símbolos obrigatórios.

**Qual a diferença entre 401 e 403?**
`401`: não sei quem você é (sem login ou token inválido). `403`: sei quem você é, mas você não tem
permissão (um Viewer tentando cadastrar uma aplicação).

**Como o CI se autentica no DeployGuard? Por que não usar o login normal?**
Com uma chave de API no cabeçalho `Authorization`. O CI não tem navegador para guardar cookie, e
colocar a senha de uma pessoa no pipeline misturaria identidades e daria acesso total. A chave tem
escopo mínimo (só registrar deploy), pode ser revogada sozinha e é auditada.

**Como você guarda as chaves de API?**
Só o hash SHA-256. A chave é mostrada uma vez na criação. Como ela tem 256 bits aleatórios, hash
rápido é suficiente; senha de pessoa é que precisa de hash lento.

**Como o sistema sabe que um deploy causou o incidente?**
Ele não sabe, e isso é proposital. Ele mostra o deploy mais recente até 30 minutos antes do
incidente como **suspeito**. É uma pista que encurta a investigação, não uma conclusão.

**Como o dashboard fica em tempo real? Por que SSE e não WebSocket?**
Com Server-Sent Events: o navegador abre `GET /api/events` e a resposta não termina; o servidor
escreve um aviso a cada mudança. Escolhi SSE porque o painel só recebe dados. É HTTP puro, usa o
mesmo cookie de login, passa pelo nginx e reconecta sozinho. O aviso só diz "a aplicação X mudou",
e a tela busca os dados na API, então perder um aviso não deixa a tela errada.

**E se um cliente for lento ou abrirem mil abas?**
Cada conexão tem uma fila limitada que descarta os avisos mais antigos, então um cliente lento não
consome memória sem fim. Há um limite de conexões: acima dele a API responde 503 e o dashboard
continua funcionando com atualização a cada minuto.

**Como você escalou o DeployGuard para várias instâncias?**
A API já era stateless (JWT no cookie), então o nginx reparte as requisições. O agendador e os
alertas reservam lotes com `UPDATE ... FOR UPDATE SKIP LOCKED`: cada réplica pega linhas
diferentes, e a reserva tem um prazo, então se uma réplica cair outra assume. Os avisos ao vivo
passam pelo `LISTEN/NOTIFY` do PostgreSQL, dentro da transação, para chegar a todas as réplicas.

**Por que não usar RabbitMQ ou Redis?**
Para esse volume, o PostgreSQL resolve fila (`SKIP LOCKED`) e pub/sub (`LISTEN/NOTIFY`), e é um
serviço a menos para operar e proteger. Se o volume crescesse muito, ou se eu precisasse de rate
limit global, aí o Redis passaria a valer a pena.

**Qual bug de concorrência você encontrou?**
Ao subir duas réplicas juntas, as duas aplicaram as migrations ao mesmo tempo e uma quebrou.
Resolvi com uma advisory lock do PostgreSQL, e escrevi um teste que sobe as duas em paralelo.

**Como você protege o login contra força bruta?**
Em duas camadas: rate limit por IP e bloqueio temporário da conta depois de 5 senhas erradas. O
bloqueio é temporário para não virar ferramenta de negação de serviço, e a resposta é igual para
e-mail inexistente, senha errada e conta bloqueada, para não revelar quais contas existem.

**Se alguém descobrir a senha de um usuário, como você expulsa essa pessoa?**
O usuário troca a senha. Isso gera um novo carimbo de segurança, que vai dentro do token e é
conferido a cada requisição. Todos os tokens antigos param de valer na hora, menos o da sessão de
quem trocou, que recebe um cookie novo.

**O que vai para a auditoria e o que não vai?**
Vão logins, bloqueios, troca de senha e alterações de usuários, aplicações e chaves, com quem,
quando e o IP. Nunca vão senhas, chaves ou tokens. O registro é gravado na mesma transação da ação
e não pode ser editado pela API.

**Como você expôs dados sem login com segurança?**
Com uma rota pública separada, com DTO próprio e mínimo (sem ids, URLs ou erros), publicação
opt-in por aplicação, rate limit próprio e output cache de 30 s, para que muitos acessos não virem
muitas consultas pesadas no banco.

**Como você implementou o "esqueci minha senha" com segurança?**
Resposta igual para qualquer e-mail, envio assíncrono para não vazar pelo tempo, token aleatório de
256 bits guardado como hash, validade de 30 minutos, uso único com UPDATE atômico, link com endereço
fixo (contra host header injection) e, depois da troca, um security stamp novo que derruba todas as
sessões.

**Como o rate limit funciona com várias réplicas?**
O contador fica no Redis: um script Lua faz INCR e define a expiração atomicamente, então todas as
réplicas somam no mesmo lugar. Se o Redis cair, cada réplica volta ao limitador em memória e a API
continua no ar.

**Como você evitou que o bloqueio de conta virasse um ataque de negação de serviço?**
O bloqueio é por par conta e IP: quem erra a senha de propósito tranca só o próprio IP, e o dono
entra de outro lugar. Para ataques espalhados por muitos IPs existe um teto por conta bem mais alto.
Os contadores sobem com um upsert atômico, e o bloqueio é temporário.

**Por que a API se recusa a subir com SMTP sem TLS?**
Porque o e-mail de recuperação carrega um token que dá acesso à conta, e o login do SMTP é um
segredo. Os modos "TLS se disponível" podem ser rebaixados por quem está no meio do caminho. Sem TLS
só é aceito para um servidor local de teste, como o Mailpit.
