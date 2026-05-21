# ConvertFlac
personal challenge: rewrite the script I used for batch converting FLACs into V0 VBR MP3s in functional-style C#  
  
first argument is target folder, second is max parallelism
if no folder is specified, the user is prompted for one
if no parallelism limit is specified, it's set to the system's cpu core count divided by 1.5

runs ffmpeg parallelly to convert FLACs to V0 VBR MP3s using LAME

I just tried to be as "modern" and "functional" as possible as a personal challenge, so I used channels for parallelism and a functional, "Redux style", UI state pipeline as well as "new" C# constructs and patterns.  
the encoding task is a pure producer (aka writes to the channel), whilst the dashboard UI is a pure consumer (aka reads the channel)

UI states are calculated in a "pure", functional way, then rendered

I did my best to keep side effects isolated at the beginning and end of any path

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
