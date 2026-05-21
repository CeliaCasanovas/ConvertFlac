# ConvertFlac
personal challenge: rewrite the script I used for batch converting FLACs into V0 VBR MP3s in functional-style C#  
  
first argument is target folder, second is max parallelism

runs ffmpeg parallelly to convert FLACs to V0 VBR MP3s using LAME

I just tried to be as "modern" and "functional" as possible as a personal challenge, so I used channels for parallelism and a functional, "Redux style", UI state pipelineas well as "new" C# constructs and patterns.  
the encoding task is a pure producer (aka writes to the channel), whilst the dashboard UI is a pure consumer (aka reads the channel)

UI states are calculated in a "pure", functional way, then rendered

 I did my best to keep side effects isolated at the beginning and end of any path

args[0] is the target folder
if none is specified, the user will be prompted for one

args[1] is the parallelism limit
if none is specified, it's set to the core count of the system divided by 1.5

TODO: extract all strings so that I can easily write a Japanese and a Catalan version
TODO: extract all text colours so that they can easily be changed or themed (?)
      so far,
      business as usual is    ConsoleColor.Cyan,
      errors are              ConsoleColor.Red,
      relevant messages are   ConsoleColor.Yellow,
      success is              ConsoleColor.Green,
      and prompts use the default colour
  TODO: offer encoding options maybe? dunno
  TODO: better validation!!!!!!!!!!
