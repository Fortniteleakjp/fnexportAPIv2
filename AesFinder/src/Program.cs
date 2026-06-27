using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace AesFinder
{
    class Program
    {
        static string[] Pattrens = { 
            "C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ?",
            "C7 ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ?",
            "C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? 48 ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ?", //34.40 main key is from here
            "C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? C3"};

        static int[][] KeyOffsets = {
            new[]{ 3, 10, 17, 24, 35, 42, 49, 56 },
            new[]{ 2, 9, 16, 23, 30, 37, 44, 51 },
            new[]{ 3, 10, 21, 28, 35, 42, 49, 56 },
            new[]{ 51, 45, 38, 31, 24, 17, 10, 3 }};

        static void Main(string[] args)
        {
            string Path = "";
#if DEBUG
            Path = "C:\\Cpp\\AesFinder\\UnrealEditorFortnite-Win64-Shipping.exe";
#endif

            if (args.Length != 0)
            {
                Path = args[0];
            }

            if(Path == "")
            {
                Console.WriteLine("Error: No File Has Been Provided!");
                return;
            }

            if (!File.Exists(Path))
            {
                Console.WriteLine("Error: File Doesn't Exists!");
                return;
            }

            FileStream file = File.Open(Path, FileMode.Open);

            //getting the file size
            file.Seek(0, SeekOrigin.End);
            int fileSize = (int)file.Position;
            file.Seek(0, SeekOrigin.Begin);

            byte[] buffer = new byte[fileSize];
            file.Read(buffer, 0, fileSize);
            file.Close();

            string[] Keys = FindMainAESKey(ref buffer);

            for(int i=0;i< Keys.Length; i++)
            {
                Console.WriteLine(Keys[i]);
            }

            if(Keys.Length == 0)
            {
                Console.WriteLine("No AES Keys Have Been Found!");
            }
        }

        static string[] FindMainAESKey(ref byte[] buffer)
        {
            return FindMainAESKey(ref buffer, 2.5, false);
        }

        static string[] FindMainAESKey(ref byte[] buffer, double cutOfThreshold)
        {
            return FindMainAESKey(ref buffer, cutOfThreshold, false);
        }

        static string[] FindMainAESKey(ref byte[] buffer, bool disableZeroStreakCutOf)
        {
            return FindMainAESKey(ref buffer, 2.5, disableZeroStreakCutOf);
        }

        static string[] FindMainAESKey(ref byte[] buffer, double cutOfThreshold, bool disableZeroStreakCutOf)
        {
            string[] keys = [];

            for (int i = 0; i < Pattrens.Length; i++)
            {
                string[] Patternkeys = GetKeys(FindkeyIndexes(PattenToArray(Pattrens[i]), ref buffer), KeyOffsets[i], ref buffer);

                //resizing and adding the results together
                Array.Resize(ref keys, keys.Length + Patternkeys.Length); 
                Patternkeys.CopyTo(keys, keys.Length - Patternkeys.Length);
            }

            (string, double)[] keyEntropyPair = new (string, double)[keys.Length];
            int index = 0;

            for (int i = 0; i < keys.Length; i++)
            {
                (string, double) temp = CalculateEntropy(keys[i], disableZeroStreakCutOf);

                if (temp.Item2 >= cutOfThreshold) //not adding anything below the threshold
                {
                    keyEntropyPair[index] = temp;
                    index++;
                }
            }

            Array.Resize(ref keyEntropyPair, index); //scalling down the array based on the number of added keys;

            return SortKeyValuePair(keyEntropyPair);
        }

        static string[] GetKeys(int[] keyIndexs,int[] patternOffsets, ref byte[] buffer)
        {
            string[] stringKeys = new string[keyIndexs.Length];

            for(int i=0;i< keyIndexs.Length; i++) 
            {
                for (int j = 0; j < patternOffsets.Length; j++) //going thought the offset (always 8) then getting the hax value 
                {
                    stringKeys[i] += SpanToHexString(new Span<byte>(buffer, keyIndexs[i] + patternOffsets[j], 4));
                }
            }

            return stringKeys;
        }
        static int[] FindkeyIndexes(int[] patten,ref byte[] buffer)
        {
            int[] FoundKeysOffset = [];

            for(int i = 0; i < buffer.Length; i++)
            {   
                for (int j = 0; i + j < buffer.Length && j < patten.Length; j++)
                {
                    if (patten[j] != buffer[i + j] && patten[j] != -1) break; //not a ? or is pattern isn't found 

                    if(j == patten.Length - 1) //if its at the last letter from the pattern
                    {
                        Array.Resize(ref FoundKeysOffset, FoundKeysOffset.Length + 1);

                        FoundKeysOffset[FoundKeysOffset.Length - 1] = i;
                    }
                }
            }
            
            return FoundKeysOffset;
        }

        static int[] PattenToArray(string pattren)
        {
            int[] newPatten = [];

            int index = 0;
            string temp = "";

            for(int i = 0; i < pattren.Length; i++)
            {
                if(pattren[i] == ' ') continue; //skiping spaces

                if (pattren[i] == '?') {
                    Array.Resize(ref newPatten, newPatten.Length + 1);
                    newPatten[index] = -1; 

                    index++;
                    continue;
                }

                temp += pattren[i];

                if(temp.Length == 2) //waiting until it has two letter then getting the proper value 
                {
                    Array.Resize(ref newPatten, newPatten.Length + 1);
                    newPatten[index] = int.Parse(temp, System.Globalization.NumberStyles.HexNumber);

                    temp = "";
                    index++;
                }
            }

            return newPatten;
        }

        static string SpanToHexString(Span<byte> span)
        {
            string output = "";

            for(int i = 0; i < span.Length; i++)
            {
                output += span[i].ToString("X2");
            }

            return output;
        }

        static (string, double) CalculateEntropy(string key, bool disableZeroStreakCutOf)
        {
            Dictionary<char, int> pair = new Dictionary<char, int>(16); //16 is the number of unique hex characters

            bool isFirstStreak = false;
            int currentZeroStreaks = 0;

            if (!disableZeroStreakCutOf)
            {
                //detecting if the key is so obv wrong by checking if it has 4 zeros after each other twice
                for (int i = 0; i < key.Length; i++)
                {
                    if (key[i] == '0')
                    {
                        currentZeroStreaks++;

                        if (currentZeroStreaks == 4) //making it less might cause issues wih it filtering out too much keys
                        {
                            isFirstStreak = !isFirstStreak;

                            if (!isFirstStreak) return (key, 0); //second streak
                        }
                    }
                    else currentZeroStreaks = 0;
                }
            }

            //adding/counting unique letters
            for(int i = 0; i < key.Length; i++)
            {
                if (!pair.TryAdd(key[i], 1)) {
                    pair[key[i]]++;
                }
            }

            double Entroby = 0;

            //adding up entropy
            for (int i = 0; i < pair.Count; i++)
            {
                Entroby += (double)1 / pair[key[i]]; //by dividing you assign more value to the less repeating letters
            }

            return (key, Entroby);
        }

        static string[] SortKeyValuePair((string, double)[] keyValuePair)
        {
            keyValuePair = keyValuePair.OrderByDescending(pair => { return pair.Item2; }).ToArray();

            string[] Keys = new string[keyValuePair.Length];

            for (int i = 0; i < keyValuePair.Length; i++)
            {
                Keys[i] = "0x" + keyValuePair[i].Item1;
            }

            return Keys;
        }

    }
}