# EasyVocal

<!-- Botões de download: cada um aponta para o arquivo com esse nome na última release (feitos por tools/Publish-Release.ps1). -->
<p align="center">
  <a href="https://github.com/tarl3s/EasyVocal/releases/latest/download/EasyVocal-win-x64.zip"><img alt="⬇ Baixar para Windows" src="https://img.shields.io/badge/%E2%AC%87%20Baixar-para%20Windows-2ea44f?style=for-the-badge&logo=windows&logoColor=white" height="48"></a>
  <a href="https://github.com/tarl3s/EasyVocal/releases/latest/download/EasyVocal-macos-arm64.zip"><img alt="⬇ Baixar para macOS (Apple Silicon)" src="https://img.shields.io/badge/%E2%AC%87%20Baixar-macOS%20(Apple%20Silicon)-555555?style=for-the-badge&logo=apple&logoColor=white" height="48"></a>
  <a href="https://github.com/tarl3s/EasyVocal/releases/latest/download/EasyVocal-macos-x64.zip"><img alt="⬇ Baixar para macOS (Intel)" src="https://img.shields.io/badge/%E2%AC%87%20Baixar-macOS%20(Intel)-555555?style=for-the-badge&logo=apple&logoColor=white" height="48"></a>
  <a href="https://github.com/tarl3s/EasyVocal/releases/latest/download/EasyVocal-linux-x64.tar.gz"><img alt="⬇ Baixar para Linux" src="https://img.shields.io/badge/%E2%AC%87%20Baixar-para%20Linux-e95420?style=for-the-badge&logo=linux&logoColor=white" height="48"></a>
</p>
<p align="center">O download começa sozinho — veja na sua pasta Downloads. Ou <a href="https://github.com/tarl3s/EasyVocal/releases/latest">escolha o seu arquivo na página de releases</a>.</p>
<p align="center">Uns 40 MB cada, não precisa instalar mais nada. Windows 10 ou 11 · macOS 11 ou mais novo · Linux x64 com PipeWire ou PulseAudio.<br>
<b>macOS e Linux são uma Prévia:</b> compilados e com testes automáticos, mas ainda não testados em Macs nem em
desktops Linux de verdade. Por favor, <a href="https://github.com/tarl3s/EasyVocal/issues">avise se der problema</a>.</p>

**Um trocador de voz e painel de sons, grátis e leve, para qualquer chamada.** Fale como robô, monstro ou esquilo
no Discord, no WhatsApp Desktop, nos jogos, em qualquer lugar que use microfone, e toque sons na chamada com uma
tecla. Sem anúncios, sem pagar, nada bloqueado.

[English](README.md)

## Configure em 3 passos

O EasyVocal manda a sua voz modificada para um **microfone virtual**, e o app da chamada escuta esse microfone.

### Windows

1. **Instale o EasyVocal.** Extraia o download em qualquer pasta e abra o `EasyVocal.exe`.
2. **Instale o VB-CABLE de dentro do app.** Na primeira vez o EasyVocal explica e tem um botão **Instalar**.
   Se você não ouvir nada depois de instalar (o VB-CABLE pode virar o alto-falante padrão do PC), o EasyVocal volta o
   som para o seu fone ou caixas de som, ou mostra um botão **Resolver** que faz isso.
3. **Escolha "CABLE Output" como microfone** no app da chamada (Discord, WhatsApp Desktop, seu jogo...). O EasyVocal
   mostra um guia curto para cada app.

### macOS (Prévia)

1. **Instale o EasyVocal.** Extraia o download (Apple Silicon para Macs M1/M2/M3/M4, Intel para os mais antigos) e
   arraste o `EasyVocal.app` para **Aplicativos**. O EasyVocal ainda não é assinado pela Apple, então na primeira vez
   **clique com o botão direito → Abrir → Abrir** (no macOS 15, se não aparecer o botão Abrir: Ajustes do Sistema →
   Privacidade e Segurança → **Abrir Mesmo Assim**).
   Se o macOS disser que o EasyVocal "está danificado" ou ele fechar na hora, abra o **Terminal** uma vez e cole:
   ```
   xattr -dr com.apple.quarantine /Applications/EasyVocal.app
   codesign --force --deep --sign - /Applications/EasyVocal.app
   ```
   (a primeira linha diz ao macOS que você confia no download; a segunda dá a assinatura local que o Apple Silicon exige).
2. **Permita o microfone** quando o macOS pedir (ou depois em Ajustes do Sistema → Privacidade e Segurança → Microfone).
3. **Instale o BlackHole 2ch** (um microfone virtual grátis): o EasyVocal instala pelo Homebrew se você tiver, ou abre
   a página oficial. Depois **escolha "BlackHole 2ch" como microfone** no app da chamada.

### Linux (Prévia)

1. **Instale o EasyVocal.** Extraia o download (`tar xzf EasyVocal-linux-x64.tar.gz`) e rode
   `EasyVocal/install.sh` (copia para `~/.local/share/EasyVocal` e põe no menu de aplicativos, sem precisar de admin)
   ou só abra `./EasyVocal/EasyVocal`.
2. **Precisa de PipeWire ou PulseAudio com o `pactl`** (a maioria dos desktops tem; senão instale o `pulseaudio-utils`).
   Nada mais é instalado: enquanto roda, o EasyVocal cria o próprio microfone virtual.
3. **Escolha "EasyVocal-Microphone" como microfone** no app da chamada.

Os atalhos globais precisam de uma sessão X11 (Xorg); no Wayland, use os botões da janela.

Pronto: o que você faz no EasyVocal é o que o outro lado da chamada escuta.

> **A voz só funciona com o EasyVocal aberto.** Fechar a janela manda o app para a bandeja (perto do relógio, ou a
> barra de menus no Mac), onde ele continua funcionando; num Linux sem ícones na bandeja ela é minimizada, e as
> configurações têm **Sair do EasyVocal**. Se você sair de vez, suas chamadas não ouvem nada até você
> abrir de novo. Nas configurações dá para marcar **Iniciar com o Windows** (no Mac ou Linux: iniciar ao entrar) para
> nunca esquecer.

## O que vem nele

- **14 vozes prontas, cada uma com seu jeito:**
  - **Normal**: a sua voz, sem mudar nada.
  - **Feminina** / **Masculina**: mais aguda, com o corpo da voz mais leve e menos grave / mais grave, com o corpo
    maior e mais peito.
  - **Robô**: a melodia da sua fala achatada em quase uma nota só, metálica e zumbindo.
  - **Robô de filme**: um sintetizador zumbindo numa nota fixa que fala as suas palavras (vocoder).
  - **Esquilo**: bem aguda e pequenininha.
  - **Monstro**: grave, áspera e rosnando, com muito grave e quase sem eco.
  - **Demônio**: a sua voz mais uma segunda voz uma oitava abaixo, distorcida, numa caverna escura.
  - **Gigante**: um corpo enorme, um pouco mais grave, num salão gigantesco; limpa.
  - **Anão**: um pouco mais grave, com um corpo pequeno e rouco.
  - **Alienígena**: um tom que oscila e treme, com um toque metálico e um eco espacial.
  - **Fantasma**: um sussurro cheio de ar que oscila de leve, flutuando numa sala grande e suave.
  - **Rádio**: um rádio antigo fininho, com um pouco de chiado e estalos.
  - **Caverna**: a sua voz normal com ecos nas paredes de pedra.
- **Suas próprias vozes:** um editor pequeno com 9 controles simples (Mais grave/agudo, Corpo da voz, Robô, Eco,
  Rádio antigo, Distorção, Sussurro, Rosnado, Sala grande). Comece de qualquer voz pronta e ela mantém o seu jeito.
  Escute ao vivo com **Me ouvir** enquanto arrasta.
- **Um painel de sons:** 10 sons iniciais que já vêm no app, e os seus: adicione um arquivo de som (MP3, WAV, OGG,
  FLAC, M4A, AAC, WMA, AIFF) ou o som de um vídeo (MP4, MOV, WMV...), ou só arraste e solte, e dê um nome, um emoji e
  um volume. Apertar de novo num som tocando para ele. Dá para tocar vários ao mesmo tempo. Marque
  **🎭 Sons usam a voz** e os seus sons também passam pela voz (troque de voz enquanto um som toca e ele muda junto).
- **A sua ordem:** arraste os cartões de vozes e de sons para deixá-los na ordem que quiser (ou use **Mover para a
  esquerda / direita** no menu do cartão); o menu da bandeja segue a mesma ordem.
- **Um editor de som** (**✂ Editar áudio...** em qualquer som): veja a onda, escolha um trecho com o mouse e ouça no
  seu fone, e então corte, mantenha só aquele trecho ou divida o som em dois; deixe mais rápido ou mais lento
  (mantendo o tom ou não), mais agudo ou mais grave, mais alto, mais baixo ou o mais alto possível; faça aparecer ou
  sumir aos poucos, toque de trás para frente, coloque eco, passe por qualquer uma das suas vozes ou corte o silêncio
  do começo e do fim. Desfaça e refaça quanto quiser. O seu arquivo original nunca é alterado: **Voltar ao original**
  está sempre a um clique, e **Salvar uma cópia como...** exporta em WAV ou MP3. A velocidade, o tom e a voz de um
  trecho são três ajustes feitos juntos a partir de como ele era: 0,5× e depois 3 notas acima continua 0,5×, 0,75× e depois
  2× é simplesmente 2×, e **1×** ou **Normal** desfazem só aquele ajuste.
- **Atalhos globais** para cada voz e cada som, além de ligar/desligar a voz, parar todos os sons e silenciar o
  microfone. Funcionam com o jogo em tela cheia. (Uma tecla sozinha, sem Ctrl ou Alt, deixa de digitar aquele
  caractere nos outros programas enquanto o EasyVocal roda, e o app avisa.)
- **Ícone na bandeja** (a barra de menus no Mac) com menu rápido de vozes, e **Iniciar com o Windows** / iniciar ao
  entrar (desligado por padrão).
- **Atalho no Menu Iniciar**: na primeira vez que você abre o EasyVocal, ele pergunta se quer adicionar ao Menu Iniciar (no Linux, ao
  menu de aplicativos).
- **Se atualiza sozinho:** o EasyVocal avisa quando sai uma versão nova e se atualiza com um clique (ou pule essa
  versão; dá para desligar a verificação nas configurações).
- **Cabe em telas pequenas:** toda janela muda de tamanho, rola em vez de cortar as coisas, abre dentro da sua tela
  e lembra onde você a deixou.
- **English e Português (Brasil).**
- **Leve:** download pequeno, quase nenhum uso de CPU parado, poucos por cento enquanto você fala.
- **Desligado ou Normal = sua voz limpa.** Seus amigos continuam te ouvindo. O EasyVocal sempre abre na voz **Normal**, para você
  nunca entrar numa chamada com um filtro ligado sem querer (todo o resto fica como você deixou).
- **🧠 Vozes com IA** (opcional): soe como outra pessoa, não como um efeito. Veja abaixo.

## 🧠 Vozes com IA

O EasyVocal 2.0 também muda a sua voz com IA: quem está do outro lado da chamada ouve **outra pessoa falando com as
suas palavras e a sua entonação**, e não a sua voz com um efeito. Roda no seu próprio PC (nada é enviado para lugar
nenhum) com modelos de voz RVC, no Windows, no macOS e no Linux.

- **Baixar o modo IA** uma vez pela aba Vozes (um clique, cerca de 550 MB; dá para pausar e ele continua de onde
  parou). O download é conferido (SHA-256) antes de qualquer coisa ser instalada. O EasyVocal em si continua um
  download pequeno: se você nunca ligar o modo IA, nada muda. **Configurações → Remover o modo IA…** apaga tudo e
  libera o espaço.
- **Vêm 8 vozes com IA** (🧠 nos cartões): mulheres e homens jovens, um homem de voz grave e calma, um homem de voz
  rouca, uma voz jovem e leve, uma voz neutra. Elas são feitas de uma coleção pública de vozes para pesquisa (VCTK,
  CC BY 4.0) e não soam como nenhuma das pessoas dela em particular. Escolha uma como qualquer outra voz: a voz que
  estava tocando continua enquanto ela carrega (a partir da Normal, toca uma voz provisória que já não soa como você:
  a sua própria voz nunca chega à chamada).
- **Níveis de qualidade** (Configurações → IA): **Leve** (o mais rápido, pesa pouco no PC), **Equilibrado** e
  **Máximo** (o mais realista, o mais pesado). Depois do download o EasyVocal testa o seu PC uma vez e escolhe o
  melhor nível que roda sem falhas; **Testar meu PC** faz isso de novo. Cada voz pode ter o próprio nível. A barra de
  cima mostra o atraso com que a voz com IA chega na chamada (**IA · N ms**, cerca de 0,4 a 0,6 segundo: as
  vozes com IA são para falar, não para cantar no tempo).
- **Do que o seu PC precisa:** qualquer PC de 64 bits dos últimos anos roda o **Leve** no processador (4 núcleos ou
  mais); **Equilibrado** e **Máximo** pedem um processador recente de 6 núcleos ou uma placa de vídeo. O EasyVocal
  usa a placa de vídeo quando ajuda (DirectML no Windows: AMD, Intel ou NVIDIA; Core ML no Mac; no Linux, o
  processador) e volta para o processador sozinho. Cerca de 1,4 GB livres no disco durante a instalação, 800 MB
  depois; de 0,4 a 1,5 GB de memória enquanto uma voz com IA toca.
- **Adicione as suas vozes:** **Adicionar voz com IA** aceita um arquivo de modelo RVC (`.pth` ou `.onnx`, com o
  `.index` dele se tiver, ou um `.zip` com eles) ou um link para ele (uma página do Hugging Face também serve). Não
  precisa de Python nem de PyTorch.
- **Use só vozes que você tem o direito de usar.** Não use uma voz para se passar por uma pessoa real sem o
  consentimento dela. O EasyVocal não traz vozes de famosos nem de personagens.
- **Créditos:** os modelos e os dados de voz estão em **Configurações → Créditos e licenças** e em
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) (RVC, HuBERT, RMVPE, o corpus VCTK, ONNX Runtime, DirectML). O
  pacote de IA é um download separado, numa página de release própria (`ai-pack-v1`), com as licenças dentro dele.

## Créditos

- O **VB-CABLE** é um produto grátis e separado da [VB-Audio](https://vb-audio.com/Cable/) (donationware). O
  EasyVocal não o inclui: ele baixa o instalador oficial da VB-Audio e o abre para você.
- O **BlackHole** (microfone virtual do macOS) é um produto grátis e separado da
  [Existential Audio](https://existential.audio/blackhole/) (GPL-3.0). O EasyVocal não o inclui: o Homebrew instala,
  ou você instala pela página dele.
- Áudio: [NAudio](https://github.com/naudio/NAudio) (MIT), [NLayer](https://github.com/naudio/NLayer) (MIT, MP3),
  [NVorbis](https://github.com/NVorbis/NVorbis) (MIT, OGG) com NAudio.Vorbis, e [PortAudio](https://www.portaudio.com/)
  (MIT, macOS e Linux) pelos pacotes nativos do [PortAudioSharp2](https://github.com/csukuangfj/PortAudioSharp2).
- Interface: [Avalonia](https://avaloniaui.net/) (MIT).
- Vozes com IA (o pacote de IA opcional): [RVC](https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI)
  (MIT), HuBERT (fairseq, MIT), [RMVPE](https://github.com/Dream-High/RMVPE) (Apache-2.0), vozes do
  [CSTR VCTK Corpus](https://doi.org/10.7488/ds/2645) (CC BY 4.0), [ONNX Runtime](https://onnxruntime.ai/) (MIT) e,
  no Windows, o DirectML (Microsoft). Detalhes em [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- O EasyVocal tem licença MIT. Veja [LICENSE](LICENSE).

## Para desenvolvedores

```
dotnet build
dotnet test                                            # inclui o teste de ponta a ponta (~40 s, mostra o app)
dotnet run --project src/EasyVocal -- --data <pasta de rascunho> --e2e
dotnet run --project src/EasyVocal
```

Mais em [AGENTS.md](AGENTS.md). Release: `tools/Publish-Release.ps1` gera os quatro downloads no Windows.
